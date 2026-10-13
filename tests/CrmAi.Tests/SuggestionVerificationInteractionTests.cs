using CrmAi.Application;
using CrmAi.Domain;
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
    private readonly SettingsRepository settingsRepository = new();
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
            create temporary table ai_agent_settings (
                company_id uuid, agent_key text, debounce_minutes integer, updated_at timestamptz default now()
            );
            create temporary table ai_invocation_cooldowns (
                company_id uuid, agent_key text, configuration_fingerprint text,
                blocked_until timestamptz, reason text, updated_at timestamptz default now(),
                probe_id uuid,
                primary key(company_id, agent_key, configuration_fingerprint)
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
            create temporary table whatsapp_conversations (
                id uuid, company_id uuid, contact_id uuid, summary text, last_analyzed_message_at timestamptz,
                last_analysis_status text, last_analysis_at timestamptz, updated_at timestamptz
            );
            create temporary table whatsapp_conversation_analysis_runs (
                id uuid, company_id uuid, conversation_id uuid, status text,
                last_message_id uuid, created_at timestamptz default now(), window_end_at timestamptz default now(),
                summary text, error text, updated_at timestamptz default now()
            );
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
            insert into whatsapp_conversations(id,company_id,contact_id) values ('{conversationId}', '{companyId}', '{contactId}');
            insert into instagram_conversations values ('{conversationId}', '{companyId}', '{contactId}');
            insert into ai_agent_suggestions(id, company_id, contact_id, conversation_id)
            values ('{suggestionId}', '{companyId}', '{contactId}', '{conversationId}');
            """);
        worker = new(db, settingsRepository, client, Options.Create(new RabbitMqOptions()),
            NullLogger<SuggestionCompletionVerificationProcessor>.Instance, Options.Create(new OpenAiRiskAnalysisOptions()),
            new PostgresAiInvocationGuard(db));
    }

    public async Task DisposeAsync()
    {
        if (db is not null) await db.DisposeAsync();
    }

    [SuggestionPostgresFact]
    public async Task Batch_completion_preflight_requires_the_exact_run_and_tenant_and_keeps_pending_work()
    {
        await Sql($"insert into whatsapp_conversation_analysis_runs(id,company_id,conversation_id,status) values ('{suggestionId}','{companyId}','{conversationId}','completed')");
        var batch = new OpportunityEvent(Guid.NewGuid().ToString(), "opportunity.whatsapp.conversation.batch",
            DateTime.UtcNow, string.Empty, null, new Dictionary<string, object?>
            {
                ["runId"] = suggestionId.ToString(), ["companyId"] = companyId.ToString(),
                ["conversationId"] = conversationId.ToString()
            });
        var store = new PostgresWhatsappConversationActionStore(db!);
        Assert.True(await store.IsCompletedAsync(batch, ct));
        foreach (var key in new[] { "runId", "companyId", "conversationId" })
        {
            var data = new Dictionary<string, object?>(batch.Data) { [key] = Guid.NewGuid().ToString() };
            Assert.False(await store.IsCompletedAsync(batch with { Data = data }, ct));
        }
        foreach (var status in new[] { "queued", "processing", "failed", "deferred" })
        {
            await Sql($"update whatsapp_conversation_analysis_runs set status='{status}'");
            Assert.False(await store.IsCompletedAsync(batch, ct));
        }
        await Sql("update whatsapp_conversation_analysis_runs set status='superseded'");
        Assert.True(await store.IsCompletedAsync(batch, ct));
    }

    [SuggestionPostgresFact]
    public async Task Completed_newer_batch_supersedes_only_older_deferred_versions_of_the_same_last_message()
    {
        var newerRun = Guid.NewGuid();
        var otherWindow = Guid.NewGuid();
        var pendingCopies = new[] { (Id: Guid.NewGuid(), Status: "queued"),
            (Id: Guid.NewGuid(), Status: "processing"), (Id: Guid.NewGuid(), Status: "failed") };
        var lastMessage = Guid.NewGuid();
        await Sql($"""
            insert into whatsapp_conversation_analysis_runs(id,company_id,conversation_id,status,last_message_id,created_at)
            values ('{suggestionId}','{companyId}','{conversationId}','deferred','{lastMessage}',now()-interval '20 minutes'),
                   ('{newerRun}','{companyId}','{conversationId}','deferred','{lastMessage}',now()),
                   ('{otherWindow}','{companyId}','{conversationId}','deferred',gen_random_uuid(),now()-interval '30 minutes');
            """);
        foreach (var copy in pendingCopies)
            await Sql($"insert into whatsapp_conversation_analysis_runs(id,company_id,conversation_id,status,last_message_id,created_at) values ('{copy.Id}','{companyId}','{conversationId}','{copy.Status}','{lastMessage}',now()-interval '10 minutes')");
        await CompleteRun(suggestionId); // The older batch must not discard the updated audio/text in the newer one.
        Assert.Equal("deferred", await Scalar<string>($"select status from whatsapp_conversation_analysis_runs where id='{newerRun}'"));
        await Sql($"update whatsapp_conversation_analysis_runs set status='deferred' where id='{suggestionId}'");
        await CompleteRun(newerRun);
        Assert.Equal("superseded", await Scalar<string>($"select status from whatsapp_conversation_analysis_runs where id='{suggestionId}'"));
        Assert.Equal("completed", await Scalar<string>($"select status from whatsapp_conversation_analysis_runs where id='{newerRun}'"));
        Assert.Equal("deferred", await Scalar<string>($"select status from whatsapp_conversation_analysis_runs where id='{otherWindow}'"));
        foreach (var copy in pendingCopies)
            Assert.Equal("superseded", await Scalar<string>($"select status from whatsapp_conversation_analysis_runs where id='{copy.Id}'"));
    }

    private async Task CompleteRun(Guid runId)
    {
        await using var connection = await db!.OpenConnectionAsync();
        var complete = typeof(PostgresWhatsappConversationActionStore).GetMethod("CompleteQueuedRunAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)complete.Invoke(null, [connection, runId, conversationId, "Resumo de fixture", ct])!;
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
        Assert.False(await Process()); // Collect the new evidence within the configured window.
        await Due();
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
        client.DuringAnalysis = async () =>
        {
            await Message("now()");
            await RequestForScope();
        };
        Assert.True(await Process());
        Assert.True(await Scalar<bool>("select max(created_at) > (select last_verified_at from ai_agent_suggestions) from whatsapp_messages"));
        Assert.True(await Scalar<bool>("select next_verification_at between now()+interval '4 minutes' and now()+interval '6 minutes' from ai_agent_suggestions"));
        Assert.False(await Process());
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
        var retryAt = await Scalar<DateTime>("select next_verification_at from ai_agent_suggestions");
        await RequestForScope();
        Assert.Equal(retryAt, await Scalar<DateTime>("select next_verification_at from ai_agent_suggestions"));
        Assert.False(await Process());
        await Due();
        Assert.True(await Process());
        Assert.Equal("unfulfilled", await Status());
        Assert.Equal(2, client.Calls);
    }

    [SuggestionPostgresFact]
    public async Task Scheduling_uses_tenant_debounce_and_bursts_keep_the_first_deadline()
    {
        await Sql($"""
            insert into ai_agent_settings(company_id,agent_key,debounce_minutes)
            values (null,'suggestion-completion-verification',30),
                   ('{companyId}','suggestion-completion-verification',7),
                   (gen_random_uuid(),'suggestion-completion-verification',1);
            """);
        await RequestForScope();
        var first = await Scalar<DateTime>("select next_verification_at from ai_agent_suggestions");
        Assert.True(await Scalar<bool>("select next_verification_at between now()+interval '6 minutes' and now()+interval '8 minutes' from ai_agent_suggestions"));
        await RequestForScope();
        Assert.Equal(first, await Scalar<DateTime>("select next_verification_at from ai_agent_suggestions"));
        Assert.False(await Process());
        await Due();
        var elapsed = await Scalar<DateTime>("select next_verification_at from ai_agent_suggestions");
        await RequestForScope(); // A busy worker cannot let subsequent events postpone an elapsed window.
        Assert.Equal(elapsed, await Scalar<DateTime>("select next_verification_at from ai_agent_suggestions"));
        Assert.True(await Process());
    }

    [SuggestionPostgresFact]
    public async Task Scheduling_preserves_the_active_processing_lease_and_events_after_its_call()
    {
        await Sql("update ai_agent_suggestions set verification_status='processing', updated_at=now(), next_verification_at=now()+interval '15 minutes'");
        var lease = await Scalar<DateTime>("select updated_at from ai_agent_suggestions");
        await RequestForScope();
        Assert.Equal(lease, await Scalar<DateTime>("select updated_at from ai_agent_suggestions"));
        await Due();
        Assert.False(await Process());
        Assert.Equal(0, client.Calls);
    }

    [SuggestionPostgresFact]
    public async Task Missing_credentials_defer_without_call_history_or_attempts_and_resume_after_configuration_change()
    {
        await Message("now() - interval '1 day'");
        settingsRepository.ApiKey = null;
        Assert.True(await Process());
        Assert.Equal("pending", await Status());
        Assert.Equal(0, client.Calls);
        Assert.Equal(0L, await HistoryCount());
        Assert.Equal(0, await Attempts());
        Assert.Equal(1L, await Scalar<long>("select count(*) from ai_invocation_cooldowns"));
        Assert.True(await Scalar<bool>("select next_verification_at > now()+interval '59 minutes' from ai_agent_suggestions"));
        Assert.False(await Process());
        settingsRepository.ApiKey = "new-test-credential";
        await RequestForScope();
        await Due();
        Assert.True(await Process());
        Assert.Equal(1, client.Calls);
        Assert.Equal(1L, await HistoryCount());
        Assert.Equal(1, await Attempts());
        Assert.Equal(1L, await Scalar<long>("select count(*) from ai_invocation_cooldowns where reason='credential_missing'"));
    }

    [SuggestionPostgresFact]
    public async Task Quota_failure_is_recorded_once_and_new_events_cannot_cancel_its_cooldown()
    {
        await Message("now() - interval '1 day'");
        client.ExceptionOnce = new OpenAiRequestException("Provider quota exhausted", 429, "credit_balance_exhausted");
        Assert.True(await Process());
        Assert.Equal("failed", await Status());
        Assert.Equal(1, client.Calls);
        Assert.Equal(1L, await HistoryCount());
        var retryAt = await Scalar<DateTime>("select next_verification_at from ai_agent_suggestions");
        await RequestForScope();
        Assert.Equal(retryAt, await Scalar<DateTime>("select next_verification_at from ai_agent_suggestions"));
        Assert.False(await Process());
        await Due(); // Even if another caller prematurely releases the suggestion, the persistent guard holds.
        Assert.True(await Process());
        Assert.Equal(1, client.Calls);
        Assert.Equal(1L, await HistoryCount());
        Assert.Equal(1, await Attempts());
        await Sql("update ai_invocation_cooldowns set blocked_until=now()-interval '1 second'");
        await Due();
        Assert.True(await Process());
        Assert.Equal(2, client.Calls);
        Assert.Equal("unfulfilled", await Status());
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
        public string? ApiKey { get; set; } = "test-key";
        public Task<AiAgentRuntimeSettings> GetAsync(string agentKey, string? companyId, CancellationToken cancellationToken) =>
            Task.FromResult(AiContextOptimizationTests.Settings() with
            {
                AgentKey = agentKey, ApiKey = ApiKey,
                ContextEntityKeys = ["activities", "notes", "whatsapp_messages", "instagram_messages", "opportunities", "history", "meeting_analysis"]
            });
    }
    private sealed class TrackingClient : IOpenAiSuggestionCompletionVerificationClient
    {
        public int Calls { get; private set; }
        public bool Fulfill { get; set; }
        public bool FailOnce { get; set; }
        public Exception? ExceptionOnce { get; set; }
        public Func<Task>? DuringAnalysis { get; set; }
        public async Task<SuggestionCompletionVerificationResult> AnalyzeAsync(AiAgentRuntimeSettings settings,
            SuggestionCompletionVerificationInput input, AiAgentInvocationContext invocationContext, CancellationToken cancellationToken)
        {
            Calls++;
            if (ExceptionOnce is { } exception)
            {
                ExceptionOnce = null;
                throw exception;
            }
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
