using CrmAi.Application;
using CrmAi.Infrastructure.Persistence;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CrmAi.Tests;

public sealed class SuggestionPostgresFactAttribute : FactAttribute
{
    public SuggestionPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUGGESTION_VERIFICATION_TEST_DATABASE")))
            Skip = "Requires a PostgreSQL connection; fixtures use only session-local temporary tables.";
    }
}

/// <summary>
/// Every application table is temporary, search_path is pg_temp, and fixture
/// DML runs read-only (PostgreSQL permits writes only to temporary tables).
/// A private single-connection pool keeps these tables alive between calls.
/// No schema, business data, provider API or RabbitMQ is modified by these tests.
/// </summary>
public sealed class SuggestionVerificationInteractionTests : IAsyncLifetime
{
    private NpgsqlDataSource? db;
    private SuggestionCompletionVerificationProcessor worker = null!;
    private readonly TrackingClient client = new();
    private readonly Guid companyId = Guid.NewGuid();
    private readonly Guid contactId = Guid.NewGuid();
    private readonly Guid conversationId = Guid.NewGuid();
    private readonly Guid suggestionId = Guid.NewGuid();
    private readonly CancellationToken ct = CancellationToken.None;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("SUGGESTION_VERIFICATION_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var configuration = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "pg_temp", MaxPoolSize = 1, MinPoolSize = 1,
            NoResetOnClose = true, ApplicationName = "Skopos.Verification.Interaction.Tests"
        };
        db = NpgsqlDataSource.Create(configuration.ConnectionString);
        await Sql("""
            create temporary table ai_agent_suggestions (
                id uuid primary key, company_id uuid, contact_id uuid, conversation_id uuid,
                suggestion_type text default 'activity', status text default 'pending',
                title text default 'Retornar ao contato', description text default 'Confirmar o retorno',
                created_at timestamptz default now() - interval '2 days', suggested_due_at timestamptz,
                payload jsonb default '{}', verification_status text default 'pending', evidence_fingerprint text,
                verification_attempt_count integer default 0, next_verification_at timestamptz,
                last_verified_at timestamptz, updated_at timestamptz default now(), priority_at timestamptz,
                resolved_at timestamptz, priority_notified_at timestamptz, verification_confidence integer,
                verification_reason text, verification_model text, verification_evidence jsonb default '[]'
            );
            create temporary table ai_agent_suggestion_verifications (
                id uuid, company_id uuid, suggestion_id uuid, result text, confidence integer, reason text,
                evidence jsonb, evidence_fingerprint text, model text, prompt_fingerprint text, created_at timestamptz
            );
            create temporary table contacts (id uuid, company_id uuid, account_id uuid);
            create temporary table opportunities (
                id uuid, company_id uuid, account_id uuid, name text, status text,
                updated_at timestamptz default now(), created_at timestamptz default now()
            );
            create temporary table opportunity_contacts (opportunity_id uuid, contact_id uuid);
            create temporary table activities (
                id uuid default gen_random_uuid(), company_id uuid, contact_id uuid, opportunity_id uuid,
                title text default 'Retorno', activity_type text default 'call', channel text default 'phone',
                status text default 'done', completed_notes text default 'Retorno registrado', notes text,
                updated_at timestamptz default now(), created_at timestamptz default now()
            );
            create temporary table whatsapp_conversations (id uuid, company_id uuid, contact_id uuid);
            create temporary table instagram_conversations (id uuid, company_id uuid, contact_id uuid);
            create temporary table whatsapp_messages (
                id uuid default gen_random_uuid(), conversation_id uuid, message_at timestamptz,
                direction text default 'incoming', message_type text default 'text', text text default 'Novo retorno',
                created_at timestamptz, updated_at timestamptz
            );
            create temporary table instagram_messages (like whatsapp_messages including defaults);
            create temporary table whatsapp_message_audio_transcriptions (
                whatsapp_message_id uuid, status text default 'ready', transcript text, updated_at timestamptz default now()
            );
            create temporary table notes (
                id uuid default gen_random_uuid(), company_id uuid, contact_id uuid, opportunity_id uuid,
                account_id uuid, text text, created_at timestamptz default now(), updated_at timestamptz default now()
            );
            create temporary table opportunity_history (
                id uuid default gen_random_uuid(), company_id uuid, opportunity_id uuid, event text,
                created_at timestamptz default now()
            );
            create temporary table meeting_audio_recordings (
                id uuid default gen_random_uuid(), company_id uuid, activity_id uuid, opportunity_id uuid,
                transcribed_at timestamptz, updated_at timestamptz default now(), created_at timestamptz default now(),
                summary text, transcript text
            );
            set default_transaction_read_only = on;
            """);
        Assert.Equal("on", await Scalar<string>("select current_setting('transaction_read_only')"));
        Assert.Equal("pg_temp", await Scalar<string>("show search_path"));
        await Sql($"""
            insert into contacts values ('{contactId}', '{companyId}', null);
            insert into whatsapp_conversations values ('{conversationId}', '{companyId}', '{contactId}');
            insert into instagram_conversations values ('{conversationId}', '{companyId}', '{contactId}');
            insert into ai_agent_suggestions(id, company_id, contact_id, conversation_id)
            values ('{suggestionId}', '{companyId}', '{contactId}', '{conversationId}');
            """);
        worker = new(db, new SettingsRepository(), client, Options.Create(new RabbitMqOptions()),
            NullLogger<SuggestionCompletionVerificationProcessor>.Instance, Options.Create(new OpenAiRiskAnalysisOptions()));
    }

    public async Task DisposeAsync()
    {
        if (db is not null) await db.DisposeAsync();
    }

    [SuggestionPostgresFact]
    public async Task No_interaction_is_recorded_once_and_periodic_ticks_or_automatic_activities_do_not_reopen_it()
    {
        await Message("now() - interval '49 hours'"); // Only context before the suggestion.
        await Activity("agent-skopos");
        Assert.True(await Process());
        Assert.Equal(0, client.Calls);
        Assert.Equal(1L, await HistoryCount());
        for (var tick = 0; tick < 4; tick++)
        {
            await Due();
            await Sql("update activities set notes='Resumo automático atualizado', updated_at=now()");
            Assert.False(await Process());
        }
        Assert.Equal(1L, await HistoryCount());
        Assert.Equal(1, await Attempts());
        Assert.Equal("unfulfilled", await Status());
    }

    [SuggestionPostgresFact]
    public async Task A_new_whatsapp_interaction_reopens_the_check_but_an_unchanged_analysis_does_not()
    {
        await Message("now() - interval '1 day'");
        Assert.True(await Process());
        Assert.Equal(1, client.Calls);
        await RequestForScope();
        Assert.False(await Process());
        await Message("now()");
        await RequestForScope();
        Assert.True(await Process());
        Assert.Equal(2, client.Calls);
        Assert.Equal(2L, await HistoryCount());
        await Due();
        Assert.False(await Process());
    }

    [SuggestionPostgresFact]
    public async Task New_activity_and_equivalent_channel_completion_are_still_verified()
    {
        Assert.True(await Process());
        await Activity("call");
        await Due();
        client.Fulfill = true;
        Assert.True(await Process());
        Assert.Equal(1, client.Calls);
        Assert.Equal("fulfilled", await Scalar<string>("select status from ai_agent_suggestions"));
        await Due();
        Assert.False(await Process());
    }

    [SuggestionPostgresFact]
    public async Task An_edited_activity_can_reopen_an_existing_unfulfilled_result()
    {
        await Activity("call");
        Assert.True(await Process());
        await Sql("update activities set status='done', completed_notes='Confirmação atualizada', updated_at=now()");
        await Due();
        Assert.True(await Process());
        Assert.Equal(2, client.Calls);
    }

    [SuggestionPostgresFact]
    public async Task Delivery_metadata_changes_do_not_call_the_model_or_increment_the_actual_attempt_count()
    {
        await Message("now() - interval '1 day'");
        Assert.True(await Process());
        await Sql("update whatsapp_messages set updated_at=now()");
        await Due();
        Assert.True(await Process()); // Lightweight fingerprint comparison only.
        Assert.Equal(1, client.Calls);
        Assert.Equal(1L, await HistoryCount());
        Assert.Equal(1, await Attempts());
        await Due();
        Assert.False(await Process());
    }

    [SuggestionPostgresFact]
    public async Task Newly_ready_audio_transcription_and_instagram_messages_are_new_evidence()
    {
        await Message("now() - interval '1 day'");
        await Sql("update whatsapp_messages set message_type='audio', text=null");
        Assert.True(await Process());
        await Sql("insert into whatsapp_message_audio_transcriptions(whatsapp_message_id,transcript) select id,'Retorno confirmado na ligação' from whatsapp_messages");
        await Due();
        Assert.True(await Process());
        await Message("now()", instagram: true);
        await Due();
        Assert.True(await Process());
        Assert.Equal(3, client.Calls);
    }

    [SuggestionPostgresFact]
    public async Task Evidence_arriving_during_the_model_call_is_not_lost_by_the_completion_timestamp()
    {
        await Message("now() - interval '1 day'");
        client.DuringAnalysis = () => Message("now()");
        Assert.True(await Process());
        Assert.True(await Scalar<bool>("select max(created_at) > (select last_verified_at from ai_agent_suggestions) from whatsapp_messages"));
        await Due();
        Assert.True(await Process());
        Assert.Equal(2, client.Calls);
    }

    [SuggestionPostgresFact]
    public async Task Evidence_from_another_tenant_or_contact_does_not_reopen_the_suggestion()
    {
        Assert.True(await Process());
        await Sql($"""
            insert into whatsapp_conversations values (gen_random_uuid(),gen_random_uuid(),'{contactId}');
            insert into whatsapp_messages(conversation_id,message_at,created_at,updated_at)
            select id,now(),now(),now() from whatsapp_conversations where company_id <> '{companyId}';
            insert into activities(company_id,contact_id) values (gen_random_uuid(),'{contactId}'), ('{companyId}',gen_random_uuid());
            """);
        await Due();
        Assert.False(await Process());
        Assert.Equal(0, client.Calls);
    }

    [SuggestionPostgresFact]
    public async Task Activities_on_a_related_opportunity_are_detected_and_passive_opportunity_updates_are_ignored()
    {
        var opportunityId = Guid.NewGuid();
        await Sql($"""
            insert into opportunities(id,company_id,name,status,created_at,updated_at)
            values ('{opportunityId}','{companyId}','Negociação','open',now()-interval '49 hours',now()-interval '49 hours');
            insert into opportunity_contacts values ('{opportunityId}','{contactId}');
            """);
        Assert.True(await Process());
        await Sql("update opportunities set updated_at=now()");
        await Due();
        Assert.False(await Process());
        await Sql($"insert into activities(company_id,opportunity_id) values ('{companyId}','{opportunityId}')");
        await Due();
        Assert.True(await Process());
        Assert.Equal(1, client.Calls);
        await Sql("update opportunities set status='won', updated_at=now()");
        await Due();
        Assert.True(await Process());
        Assert.Equal(2, client.Calls);
    }

    [SuggestionPostgresFact]
    public async Task Deadline_priority_can_be_restored_without_another_model_call()
    {
        await Message("now() - interval '1 day'");
        await Sql("update ai_agent_suggestions set suggested_due_at=now()-interval '1 hour'");
        Assert.True(await Process());
        await Sql("update ai_agent_suggestions set priority_at=null");
        await Due();
        Assert.True(await Process());
        Assert.True(await Scalar<bool>("select priority_at is not null from ai_agent_suggestions"));
        Assert.Equal(1, client.Calls);
        Assert.Equal(1L, await HistoryCount());
        Assert.Equal(1, await Attempts());
        await Due();
        Assert.False(await Process());
    }

    [SuggestionPostgresFact]
    public async Task Failed_calls_can_retry_the_same_evidence_so_a_provider_outage_does_not_strand_the_suggestion()
    {
        await Message("now() - interval '1 day'");
        client.FailOnce = true;
        Assert.True(await Process());
        Assert.Equal("failed", await Status());
        await Due();
        Assert.True(await Process());
        Assert.Equal("unfulfilled", await Status());
        Assert.Equal(2, client.Calls);
    }

    [SuggestionPostgresFact]
    public async Task New_and_edited_notes_and_meeting_evidence_remain_supported()
    {
        Assert.True(await Process());
        await Sql($"insert into notes(company_id,contact_id,text) values ('{companyId}','{contactId}','Retorno registrado')");
        await Due();
        Assert.True(await Process());
        await Sql("update notes set text='Retorno corrigido',updated_at=now()");
        await Due();
        Assert.True(await Process());
        await Activity("meeting");
        await Sql($"insert into meeting_audio_recordings(company_id,activity_id,transcribed_at,transcript) select '{companyId}',id,now(),'Retorno confirmado na reunião' from activities");
        await Due();
        Assert.True(await Process());
        Assert.Equal(3, client.Calls);
    }

    private Task<bool> Process() => worker.ProcessNextAsync(ct);
    private Task Due() => Sql("update ai_agent_suggestions set next_verification_at=now()-interval '1 second'");
    private Task<long> HistoryCount() => Scalar<long>("select count(*) from ai_agent_suggestion_verifications");
    private Task<int> Attempts() => Scalar<int>("select verification_attempt_count from ai_agent_suggestions");
    private Task<string> Status() => Scalar<string>("select verification_status from ai_agent_suggestions");
    private Task Message(string at, bool instagram = false) => Sql($"""
        insert into {(instagram ? "instagram_messages" : "whatsapp_messages")}(conversation_id,message_at,created_at,updated_at)
        values ('{conversationId}',{at},{at},{at});
        """);
    private Task Activity(string type) => Sql($"insert into activities(company_id,contact_id,activity_type) values ('{companyId}','{contactId}','{type}')");
    private async Task RequestForScope()
    {
        await using var connection = await db!.OpenConnectionAsync();
        await SuggestionCompletionVerificationScheduler.RequestForScopeAsync(connection, null, companyId, contactId, null, ct);
    }
    private async Task Sql(string sql)
    {
        await using var command = db!.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
    private async Task<T> Scalar<T>(string sql)
    {
        await using var command = db!.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class SettingsRepository : IAiAgentRuntimeSettingsRepository
    {
        public Task<AiAgentRuntimeSettings> GetAsync(string agentKey, string? companyId, CancellationToken cancellationToken) =>
            Task.FromResult(AiContextOptimizationTests.Settings() with
            {
                AgentKey = agentKey, ContextEntityKeys = ["activities", "notes", "whatsapp_messages", "instagram_messages", "opportunities", "history", "meeting_analysis"]
            });
    }
    private sealed class TrackingClient : IOpenAiSuggestionCompletionVerificationClient
    {
        public int Calls { get; private set; }
        public bool Fulfill { get; set; }
        public bool FailOnce { get; set; }
        public Func<Task>? DuringAnalysis { get; set; }
        public async Task<SuggestionCompletionVerificationResult> AnalyzeAsync(AiAgentRuntimeSettings settings,
            SuggestionCompletionVerificationInput input, AiAgentInvocationContext invocationContext, CancellationToken cancellationToken)
        {
            Calls++;
            if (FailOnce)
            {
                FailOnce = false;
                throw new InvalidOperationException("Temporary provider outage (fixture)");
            }
            if (DuringAnalysis is { } callback)
            {
                DuringAnalysis = null;
                await callback();
            }
            return new(Fulfill ? "fulfilled" : "unfulfilled", 100, "Resultado simulado",
                [input.Evidence.First(evidence => !evidence.BeforeSuggestion && !evidence.IsSynthetic).Id]);
        }
    }
}
