using CrmAi.Domain;
using CrmAi.Infrastructure.Persistence;
using Npgsql;

namespace CrmAi.Tests;

[Collection("Risk integration")]
public sealed class ConversationAnalysisDeferralStoreTests : IAsyncLifetime
{
    private NpgsqlDataSource _db = null!;
    private PostgresConversationAnalysisDeferralStore _store = null!;
    private readonly Guid _company = Guid.NewGuid();
    private readonly Guid _conversation = Guid.NewGuid();
    private readonly Guid _run = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("AI_INVOCATION_GUARD_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connection)) return;
        if (new NpgsqlConnectionStringBuilder(connection).Database != "ai_invocation_guard_test")
            throw new InvalidOperationException("Not an isolated deferral test database.");
        _db = NpgsqlDataSource.Create(connection);
        _store = new(_db);
        await Sql("""
            create table if not exists whatsapp_conversations(
                id uuid primary key,company_id uuid,last_analysis_status varchar(32),
                last_analysis_at timestamptz,updated_at timestamptz not null default now());
            alter table whatsapp_conversations add column if not exists summary text;
            create table if not exists opportunities(id uuid primary key,company_id uuid not null);
            create table if not exists instagram_conversations(
                id uuid primary key,company_id uuid,channel text not null,last_analysis_status varchar(32),
                last_analysis_at timestamptz,updated_at timestamptz not null default now());
            create table if not exists whatsapp_conversation_analysis_runs(
                id uuid primary key,company_id uuid,conversation_id uuid not null,status varchar(32) not null,
                error text,updated_at timestamptz not null default now());
            truncate whatsapp_conversation_analysis_runs,whatsapp_conversations,instagram_conversations;
            """);
    }

    public async Task DisposeAsync() { if (_db is not null) await _db.DisposeAsync(); }

    [AiGuardPostgresFact]
    public async Task Whatsapp_deferral_marks_the_exact_run_and_conversation_and_is_idempotent()
    {
        await SeedWhatsapp("queued", "queued");
        var batch = Batch("whatsapp");
        await _store.MarkDeferredAsync(batch, "provider_quota_exhausted", default);
        await _store.MarkDeferredAsync(batch, "provider_quota_exhausted", default);

        Assert.Equal("deferred", await Scalar<string>("select status from whatsapp_conversation_analysis_runs"));
        Assert.Equal("deferred", await Scalar<string>("select last_analysis_status from whatsapp_conversations"));
        Assert.Equal("provider_quota_exhausted", await Scalar<string>("select error from whatsapp_conversation_analysis_runs"));
    }

    [AiGuardPostgresFact]
    public async Task Completed_work_and_new_audio_requested_state_are_preserved()
    {
        await SeedWhatsapp("completed", "completed");
        await _store.MarkDeferredAsync(Batch("whatsapp"), "provider_quota_exhausted", default);
        Assert.Equal("completed", await Scalar<string>("select status from whatsapp_conversation_analysis_runs"));
        Assert.Equal("completed", await Scalar<string>("select last_analysis_status from whatsapp_conversations"));

        await Sql("update whatsapp_conversation_analysis_runs set status='queued';update whatsapp_conversations set last_analysis_status='requested'");
        await _store.MarkDeferredAsync(Batch("whatsapp"), "provider_quota_exhausted", default);
        Assert.Equal("deferred", await Scalar<string>("select status from whatsapp_conversation_analysis_runs"));
        Assert.Equal("requested", await Scalar<string>("select last_analysis_status from whatsapp_conversations"));
    }

    [AiGuardPostgresFact]
    public async Task Wrong_tenant_or_event_run_identity_cannot_change_operational_state()
    {
        await SeedWhatsapp("queued", "queued");
        var original = Batch("whatsapp");
        var wrongTenant = new Dictionary<string, object?>(original.Data) { ["companyId"] = Guid.NewGuid().ToString() };
        await _store.MarkDeferredAsync(original with { Data = wrongTenant }, "provider_quota_exhausted", default);
        await _store.MarkDeferredAsync(original with { EventId = Guid.NewGuid().ToString() }, "provider_quota_exhausted", default);
        Assert.Equal("queued", await Scalar<string>("select status from whatsapp_conversation_analysis_runs"));
        Assert.Equal("queued", await Scalar<string>("select last_analysis_status from whatsapp_conversations"));
    }

    [AiGuardPostgresFact]
    public async Task Instagram_deferral_is_scoped_to_tenant_channel_and_preserves_completion()
    {
        await using (var insert = _db.CreateCommand("insert into instagram_conversations(id,company_id,channel,last_analysis_status) values(@conversation,@company,'instagram','queued')"))
        {
            Keys(insert);
            await insert.ExecuteNonQueryAsync();
        }
        var batch = Batch("instagram");
        var wrongTenant = new Dictionary<string, object?>(batch.Data) { ["companyId"] = Guid.NewGuid().ToString() };
        await _store.MarkDeferredAsync(batch with { Data = wrongTenant }, "credential_missing", default);
        Assert.Equal("queued", await Scalar<string>("select last_analysis_status from instagram_conversations"));
        await _store.MarkDeferredAsync(batch, "credential_missing", default);
        Assert.Equal("deferred", await Scalar<string>("select last_analysis_status from instagram_conversations"));
        await Sql("update instagram_conversations set last_analysis_status='completed'");
        await _store.MarkDeferredAsync(batch, "credential_missing", default);
        Assert.Equal("completed", await Scalar<string>("select last_analysis_status from instagram_conversations"));
        Assert.Equal(0L, await Scalar<long>("select count(*) from whatsapp_conversation_analysis_runs"));
    }

    [AiGuardPostgresFact]
    public async Task Unknown_failure_text_is_not_persisted_in_run_error()
    {
        await SeedWhatsapp("queued", "queued");
        await _store.MarkDeferredAsync(Batch("whatsapp"), "synthetic provider body with credentials", default);
        Assert.Equal("analysis_temporarily_unavailable", await Scalar<string>("select error from whatsapp_conversation_analysis_runs"));
    }

    [AiGuardPostgresFact]
    public async Task Contact_persistence_waits_for_newer_completion_then_rechecks_and_preserves_the_latest_summary()
        => await OldPersistenceCannotOverwriteNewerCompletion(contactOnly: true);

    [AiGuardPostgresFact]
    public async Task Opportunity_persistence_waits_for_newer_completion_then_rechecks_and_preserves_the_latest_summary()
        => await OldPersistenceCannotOverwriteNewerCompletion(contactOnly: false);

    [AiGuardPostgresFact]
    public async Task Canceled_acquisition_does_not_release_another_session_lock_or_leave_a_lock_in_the_waiting_session()
    {
        await using var holder = await _db.OpenConnectionAsync();
        await using var waiting = await _db.OpenConnectionAsync();
        await using var first = await PostgresConversationPersistenceLock.AcquireAsync(holder, _company, _conversation, default);
        using var cancellation = new CancellationTokenSource();
        var blocked = PostgresConversationPersistenceLock.AcquireAsync(waiting, _company, _conversation, cancellation.Token);
        await WaitForBlockedAdvisoryLock();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.False(await TryAcquire(waiting));
        await first.DisposeAsync();
        await using var acquired = await PostgresConversationPersistenceLock.AcquireAsync(waiting, _company, _conversation, default);
    }

    [AiGuardPostgresFact]
    public async Task Persistence_lock_is_released_when_the_protected_phase_throws()
    {
        await using var holder = await _db.OpenConnectionAsync();
        await using var probe = await _db.OpenConnectionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var held = await PostgresConversationPersistenceLock.AcquireAsync(holder, _company, _conversation, default);
            throw new InvalidOperationException("Synthetic persistence failure");
        });
        Assert.True(await TryAcquire(probe));
        await UnlockProbe(probe);
    }

    private async Task OldPersistenceCannotOverwriteNewerCompletion(bool contactOnly)
    {
        await SeedWhatsapp("queued", "queued");
        var opportunity = Guid.NewGuid();
        await using (var insert = _db.CreateCommand("insert into opportunities(id,company_id) values(@opportunity,@company)"))
        {
            insert.Parameters.AddWithValue("opportunity", opportunity);
            insert.Parameters.AddWithValue("company", _company);
            await insert.ExecuteNonQueryAsync();
        }
        var original = Batch("whatsapp");
        var batch = original with { Data = new Dictionary<string, object?>(original.Data) { ["contactId"] = Guid.NewGuid().ToString() } };
        var stale = new WhatsappConversationAnalysisResult("Stale summary", false, null, false, null, null, null, 80, []);
        var store = new PostgresWhatsappConversationActionStore(_db);
        await using var newer = await _db.OpenConnectionAsync();
        await using var held = await PostgresConversationPersistenceLock.AcquireAsync(newer, _company, _conversation, default);
        Task oldPersistence;
        if (contactOnly) oldPersistence = store.ApplyContactAsync(batch, stale, default);
        else
        {
            var context = RiskAnalysisStateTests.Context();
            context = context with
            {
                Opportunity = context.Opportunity with { Id = opportunity.ToString(), CompanyId = _company.ToString() },
                TriggerEvent = batch with { OpportunityId = opportunity.ToString() }
            };
            oldPersistence = store.ApplyAsync(context, stale, default);
        }
        await WaitForBlockedAdvisoryLock();
        Assert.False(oldPersistence.IsCompleted);
        await using (var completion = new NpgsqlCommand("""
            update whatsapp_conversation_analysis_runs set status='superseded' where id=@run;
            update whatsapp_conversations set summary='Latest summary',last_analysis_status='completed' where id=@conversation;
            """, newer))
        {
            completion.Parameters.AddWithValue("run", _run);
            completion.Parameters.AddWithValue("conversation", _conversation);
            await completion.ExecuteNonQueryAsync();
        }
        await held.DisposeAsync();
        await oldPersistence.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Latest summary", await Scalar<string>("select summary from whatsapp_conversations"));
        Assert.Equal("superseded", await Scalar<string>("select status from whatsapp_conversation_analysis_runs"));
        // The isolated schema contains no activities, scorecards or suggestions: any stale write would fail this test.
    }

    private async Task WaitForBlockedAdvisoryLock()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await Scalar<bool>("""
                select exists(select 1 from pg_locks where locktype='advisory' and not granted
                    and database=(select oid from pg_database where datname=current_database()));
                """)) return;
            await Task.Delay(10);
        }
        Assert.Fail("Expected a PostgreSQL advisory lock waiter before simulating newer completion.");
    }
    private async Task<bool> TryAcquire(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("select pg_try_advisory_lock(hashtextextended(@key,0));", connection);
        command.Parameters.AddWithValue("key", LockKey);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
    private async Task UnlockProbe(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("select pg_advisory_unlock(hashtextextended(@key,0));", connection);
        command.Parameters.AddWithValue("key", LockKey);
        await command.ExecuteScalarAsync();
    }
    private string LockKey => $"crm-ai:whatsapp-persistence:{_company:N}:{_conversation:N}";

    private OpportunityEvent Batch(string channel) => new(_run.ToString(), $"opportunity.{channel}.conversation.batch", DateTime.UtcNow, "", null,
        new Dictionary<string, object?> { ["companyId"] = _company.ToString(), ["conversationId"] = _conversation.ToString(), ["runId"] = _run.ToString() });
    private async Task SeedWhatsapp(string runStatus, string conversationStatus)
    {
        await using var insert = _db.CreateCommand("""
            insert into whatsapp_conversations(id,company_id,last_analysis_status) values(@conversation,@company,@conversationStatus);
            insert into whatsapp_conversation_analysis_runs(id,company_id,conversation_id,status) values(@run,@company,@conversation,@runStatus);
            """);
        Keys(insert);
        insert.Parameters.AddWithValue("run", _run);
        insert.Parameters.AddWithValue("runStatus", runStatus);
        insert.Parameters.AddWithValue("conversationStatus", conversationStatus);
        await insert.ExecuteNonQueryAsync();
    }
    private void Keys(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue("conversation", _conversation);
        command.Parameters.AddWithValue("company", _company);
    }
    private async Task Sql(string sql) { await using var command = _db.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
    private async Task<T> Scalar<T>(string sql) { await using var command = _db.CreateCommand(sql); return (T)(await command.ExecuteScalarAsync())!; }
}
