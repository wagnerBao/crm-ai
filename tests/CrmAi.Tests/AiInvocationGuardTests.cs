using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Text.Json;

namespace CrmAi.Tests;

public sealed class AiGuardPostgresFactAttribute : FactAttribute
{
    public AiGuardPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_INVOCATION_GUARD_TEST_DATABASE")))
            Skip = "Requires isolated ai_invocation_guard_test PostgreSQL.";
    }
}

[Collection("Risk integration")]
public sealed class AiInvocationGuardTests : IAsyncLifetime
{
    private NpgsqlDataSource _db = null!;
    private PostgresAiInvocationGuard _guard = null!;
    private readonly string _company = Guid.NewGuid().ToString();

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("AI_INVOCATION_GUARD_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connection)) return;
        if (new NpgsqlConnectionStringBuilder(connection).Database != "ai_invocation_guard_test")
            throw new InvalidOperationException("Not an isolated AI guard test database.");
        _db = NpgsqlDataSource.Create(connection);
        _guard = new(_db);
        await _guard.InitializeAsync(default);
        await Sql("truncate ai_invocation_cooldowns");
    }

    public async Task DisposeAsync() { if (_db is not null) await _db.DisposeAsync(); }

    [AiGuardPostgresFact]
    public async Task Missing_key_stays_deferred_after_restart_without_any_provider_request()
    {
        var missing = Settings() with { ApiKey = null };
        var first = await Assert.ThrowsAsync<AiInvocationDeferredException>(() => _guard.EnsureAvailableAsync(missing, _company, default));
        var restarted = new PostgresAiInvocationGuard(_db);
        var second = await Assert.ThrowsAsync<AiInvocationDeferredException>(() => restarted.EnsureAvailableAsync(missing, _company, default));

        Assert.Equal("credential_missing", first.Reason);
        Assert.Equal(first.RetryAtUtc, second.RetryAtUtc);
        Assert.Equal(1L, await Scalar<long>("select count(*) from ai_invocation_cooldowns"));
    }

    [AiGuardPostgresFact]
    public async Task Quota_failure_is_shared_by_company_and_agent_and_persists_after_restart()
    {
        var settings = Settings();
        var failure = await _guard.RecordFailureAsync(settings, _company,
            new OpenAiRequestException("Request failed", 429, "credit_balance_exhausted"), default);
        Assert.NotNull(failure);

        var restarted = new PostgresAiInvocationGuard(_db);
        var deferred = await Assert.ThrowsAsync<AiInvocationDeferredException>(() => restarted.EnsureAvailableAsync(settings, _company, default));
        Assert.Equal(failure.RetryAtUtc, deferred.RetryAtUtc);
        await restarted.EnsureAvailableAsync(settings, Guid.NewGuid().ToString(), default);
        await restarted.EnsureAvailableAsync(settings with { AgentKey = "instagram-conversation-analysis" }, _company, default);
        Assert.Equal(1L, await Scalar<long>("select count(*) from ai_invocation_cooldowns"));
    }

    [AiGuardPostgresFact]
    public async Task Credential_or_provider_change_reopens_on_the_next_check_without_exposing_a_secret()
    {
        var settings = Settings();
        await _guard.RecordFailureAsync(settings, _company, new OpenAiRequestException("Unauthorized", 401), default);
        var fingerprint = await Scalar<string>("select configuration_fingerprint from ai_invocation_cooldowns");
        Assert.Equal(64, fingerprint.Length);
        Assert.DoesNotContain(settings.ApiKey!, fingerprint);

        await _guard.EnsureAvailableAsync(settings with { ApiKey = "synthetic-replacement-key" }, _company, default);
        Assert.Equal(1L, await Scalar<long>("select count(*) from ai_invocation_cooldowns"));
        await _guard.RecordFailureAsync(settings, _company, new OpenAiRequestException("Unauthorized", 401), default);
        await _guard.EnsureAvailableAsync(settings with { Provider = "synthetic-other-provider" }, _company, default);
        Assert.Equal(1L, await Scalar<long>("select count(*) from ai_invocation_cooldowns"));
    }

    [AiGuardPostgresFact]
    public async Task Correcting_a_missing_key_reopens_on_the_next_check()
    {
        await Assert.ThrowsAsync<AiInvocationDeferredException>(() => _guard.EnsureAvailableAsync(Settings() with { ApiKey = " " }, _company, default));
        await _guard.EnsureAvailableAsync(Settings(), _company, default);
        Assert.Equal(1L, await Scalar<long>("select count(*) from ai_invocation_cooldowns"));
    }

    [AiGuardPostgresFact]
    public async Task Only_one_concurrent_caller_probes_an_expired_cooldown_then_success_reopens_all()
    {
        var settings = Settings();
        await _guard.RecordFailureAsync(settings, _company,
            new OpenAiRequestException("Request failed", 429, "insufficient_quota"), default);
        await Sql("update ai_invocation_cooldowns set blocked_until=now()-interval '1 second'");

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            try { return await new PostgresAiInvocationGuard(_db).EnsureAvailableAsync(settings, _company, default); }
            catch (AiInvocationDeferredException) { return null; }
        }));
        var permit = Assert.Single(results, result => result is not null);
        await _guard.RecordSuccessAsync(settings, _company, default, permit);
        await _guard.EnsureAvailableAsync(settings, _company, default);
        Assert.Equal(0L, await Scalar<long>("select count(*) from ai_invocation_cooldowns"));
    }

    [AiGuardPostgresFact]
    public async Task Rate_limiting_does_not_pause_all_company_agent_invocations()
    {
        var deferred = await _guard.RecordFailureAsync(Settings(), _company,
            new OpenAiRequestException("Request failed", 429, "rate_limit_exceeded"), default);
        Assert.Null(deferred);
        await _guard.EnsureAvailableAsync(Settings(), _company, default);
        Assert.Equal(0L, await Scalar<long>("select count(*) from ai_invocation_cooldowns"));
    }

    [AiGuardPostgresFact]
    public async Task Concurrent_success_does_not_clear_a_new_quota_failure()
    {
        var settings = Settings();
        var normalPermit = await _guard.EnsureAvailableAsync(settings, _company, default);
        await _guard.RecordFailureAsync(settings, _company,
            new OpenAiRequestException("Failed", 429, "credit_balance_exhausted"), default);

        await new PostgresAiInvocationGuard(_db).RecordSuccessAsync(settings, _company, default, normalPermit);
        await Assert.ThrowsAsync<AiInvocationDeferredException>(() => _guard.EnsureAvailableAsync(settings, _company, default));
    }

    [AiGuardPostgresFact]
    public async Task Late_failure_and_late_probe_success_cannot_replace_a_new_credential_cooldown()
    {
        var old = Settings();
        await _guard.RecordFailureAsync(old, _company, new OpenAiRequestException("Unauthorized", 401), default);
        await Sql("update ai_invocation_cooldowns set blocked_until=now()-interval '1 second'");
        var oldProbe = await _guard.EnsureAvailableAsync(old, _company, default);
        var replacement = old with { ApiKey = "synthetic-replacement-key" };
        await _guard.EnsureAvailableAsync(replacement, _company, default);
        var newer = await _guard.RecordFailureAsync(replacement, _company,
            new OpenAiRequestException("Failed", 429, "insufficient_quota"), default);
        var fingerprint = await Scalar<string>("select configuration_fingerprint from ai_invocation_cooldowns where reason='provider_quota_exhausted'");

        await new PostgresAiInvocationGuard(_db).RecordFailureAsync(old, _company, new OpenAiRequestException("Unauthorized", 401), default);
        await _guard.RecordSuccessAsync(old, _company, default, oldProbe);
        Assert.Equal(fingerprint, await Scalar<string>("select configuration_fingerprint from ai_invocation_cooldowns where reason='provider_quota_exhausted'"));
        var result = await Assert.ThrowsAsync<AiInvocationDeferredException>(() => _guard.EnsureAvailableAsync(replacement, _company, default));
        Assert.Equal(newer!.RetryAtUtc, result.RetryAtUtc);
        Assert.Equal("provider_quota_exhausted", result.Reason);
    }

    [AiGuardPostgresFact]
    public async Task Late_check_with_old_settings_cannot_remove_the_new_credential_cooldown()
    {
        var old = Settings();
        await _guard.EnsureAvailableAsync(old, _company, default);
        var replacement = old with { ApiKey = "synthetic-replacement-key" };
        await new PostgresAiInvocationGuard(_db).EnsureAvailableAsync(replacement, _company, default);
        var newer = await _guard.RecordFailureAsync(replacement, _company,
            new OpenAiRequestException("Failed", 429, "insufficient_quota"), default);

        await new PostgresAiInvocationGuard(_db).EnsureAvailableAsync(old, _company, default);
        var result = await Assert.ThrowsAsync<AiInvocationDeferredException>(() => _guard.EnsureAvailableAsync(replacement, _company, default));
        Assert.Equal(newer!.RetryAtUtc, result.RetryAtUtc);
    }

    [AiGuardPostgresFact]
    public async Task Whatsapp_agent_skips_provider_when_missing_key_and_when_another_batch_already_exhausted_quota()
    {
        var settings = new SettingsRepository { Current = Settings() with { ApiKey = null } };
        var client = new QuotaClient();
        var agent = new WhatsappConversationAnalysisAgent(client, settings, new SuggestionContext(), invocationGuard: _guard);
        var batch = new OpportunityEvent("synthetic-event", "opportunity.whatsapp.conversation.batch", DateTime.UtcNow, "", null,
            new Dictionary<string, object?> { ["companyId"] = _company, ["conversationId"] = Guid.NewGuid().ToString(), ["text"] = "Synthetic customer transcript" });

        await Assert.ThrowsAsync<AiInvocationDeferredException>(() => agent.AnalyzeContactAsync(batch, default));
        Assert.Equal(0, client.Calls);
        settings.Current = Settings();
        await Assert.ThrowsAsync<AiInvocationDeferredException>(() => agent.AnalyzeContactAsync(batch, default));
        Assert.Equal(1, client.Calls);
        var restarted = new WhatsappConversationAnalysisAgent(client, settings, new SuggestionContext(),
            invocationGuard: new PostgresAiInvocationGuard(_db));
        await Assert.ThrowsAsync<AiInvocationDeferredException>(() => restarted.AnalyzeContactAsync(batch with { EventId = "another-batch" }, default));
        Assert.Equal(1, client.Calls);
    }

    [AiGuardPostgresFact]
    public async Task Legacy_whatsapp_scheduler_keeps_the_original_batch_when_deferring_until_recovery()
    {
        await Sql("""
            create table if not exists ai_insights(
                id uuid primary key, opportunity_id uuid, kind text not null, status text not null,
                message text not null, updated_at timestamptz not null default now());
            alter table ai_insights add column if not exists opportunity_id uuid;
            create table if not exists opportunities(id uuid primary key, company_id uuid not null);
            create table if not exists ai_agent_settings(
                agent_key text not null, company_id uuid, debounce_minutes integer not null,
                is_active boolean not null, updated_at timestamptz not null default now());
            """);
        var id = Guid.NewGuid();
        var opportunityId = Guid.NewGuid();
        var conversationId = Guid.NewGuid().ToString();
        await using (var insert = _db.CreateCommand("""
            insert into opportunities(id,company_id) values(@opportunity,@company);
            insert into ai_agent_settings(agent_key,company_id,debounce_minutes,is_active)
                values('whatsapp-conversation-analysis',@company,1,true);
            insert into ai_insights(id,opportunity_id,kind,status,message)
                values(@id,@opportunity,'whatsapp-conversation-analysis','processing',@message);
            """))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("opportunity", opportunityId);
            insert.Parameters.AddWithValue("company", Guid.Parse(_company));
            insert.Parameters.AddWithValue("message", JsonSerializer.Serialize(new { conversationId, latestEventId = "synthetic-event", latestText = "Synthetic conversation to preserve" }));
            await insert.ExecuteNonQueryAsync();
        }
        var retryAt = DateTime.UtcNow.AddHours(1);
        var scheduler = new PostgresWhatsappConversationAnalysisScheduler(_db, new ConfigurationBuilder().Build());

        await scheduler.DeferAsync(id.ToString(), retryAt, "provider_quota_exhausted", default);
        await using (var age = _db.CreateCommand("update ai_insights set updated_at=now()-interval '20 minutes' where id=@id"))
        {
            age.Parameters.AddWithValue("id", id);
            await age.ExecuteNonQueryAsync();
        }
        Assert.Empty(await scheduler.ClaimDueAsync(20, default));

        await using var read = _db.CreateCommand("select status,message from ai_insights where id=@id");
        read.Parameters.AddWithValue("id", id);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("pending", reader.GetString(0));
        using var payload = JsonDocument.Parse(reader.GetString(1));
        Assert.Equal(conversationId, payload.RootElement.GetProperty("conversationId").GetString());
        Assert.Equal("synthetic-event", payload.RootElement.GetProperty("latestEventId").GetString());
        Assert.Equal("Synthetic conversation to preserve", payload.RootElement.GetProperty("latestText").GetString());
        Assert.InRange(Math.Abs((payload.RootElement.GetProperty("nextAttemptUtc").GetDateTimeOffset().UtcDateTime - retryAt).TotalMilliseconds), 0, 1);
    }

    private static AiAgentRuntimeSettings Settings() =>
        new("whatsapp-conversation-analysis", true, "OpenAI", "synthetic-model", "synthetic-test-key", "", 10, null, []);
    private async Task Sql(string sql) { await using var command = _db.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
    private async Task<T> Scalar<T>(string sql) { await using var command = _db.CreateCommand(sql); return (T)(await command.ExecuteScalarAsync())!; }
    private sealed class SettingsRepository : IAiAgentRuntimeSettingsRepository
    {
        public AiAgentRuntimeSettings Current = null!;
        public Task<AiAgentRuntimeSettings> GetAsync(string agentKey, string? companyId, CancellationToken cancellationToken) => Task.FromResult(Current);
    }
    private sealed class SuggestionContext : IWhatsappSuggestionContextRepository
    {
        public Task<WhatsappSuggestionSemanticContext> GetAsync(string? companyId, string? contactId, CancellationToken cancellationToken) =>
            Task.FromResult(WhatsappSuggestionSemanticContext.Empty);
    }
    private sealed class QuotaClient : IOpenAiWhatsappConversationAnalysisClient
    {
        public int Calls;
        public Task<OpenAiWhatsappConversationAnalysisResponse> AnalyzeAsync(AiAgentRuntimeSettings settings, WhatsappConversationAnalysisInput input,
            AiAgentInvocationContext invocationContext, CancellationToken cancellationToken)
        {
            Calls++;
            throw new OpenAiRequestException("Synthetic quota error", 429, "credit_balance_exhausted");
        }
    }
}
