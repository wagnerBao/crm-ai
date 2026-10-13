using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmAi.Application;
using Npgsql;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresAiInvocationGuard(NpgsqlDataSource dataSource) : IAiInvocationGuard
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var stream = typeof(PostgresAiInvocationGuard).Assembly.GetManifestResourceStream("AiInvocationCooldowns.sql")
            ?? throw new InvalidOperationException("AI invocation cooldown schema is missing.");
        using var reader = new StreamReader(stream);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select pg_advisory_xact_lock(610132026);" +
            await reader.ReadToEndAsync(cancellationToken), connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AiInvocationPermit> EnsureAvailableAsync(AiAgentRuntimeSettings settings, string? companyId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select configuration_fingerprint, blocked_until, reason, now()
            from ai_invocation_cooldowns
            where company_id=@company and agent_key=@agent and configuration_fingerprint=@fingerprint for update;
            """, connection, transaction);
        Keys(command, settings, companyId);
        var fingerprint = Fingerprint(settings);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        DateTime? blockedUntil = null;
        string? reason = null;
        var now = DateTime.UtcNow;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                now = reader.GetDateTime(3);
                blockedUntil = reader.GetDateTime(1);
                reason = reader.GetString(2);
            }
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            // Local configuration checks never invoke or log a failed provider request.
            blockedUntil = blockedUntil > now ? blockedUntil : now.AddHours(1);
            reason = "credential_missing";
            command.CommandText = UpsertSql;
            command.Parameters.AddWithValue("until", blockedUntil.Value);
            command.Parameters.AddWithValue("reason", reason);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new AiInvocationDeferredException(blockedUntil.Value, reason);
        }

        if (blockedUntil > now)
        {
            await transaction.CommitAsync(cancellationToken);
            throw new AiInvocationDeferredException(blockedUntil.Value, reason!);
        }

        Guid? probeId = null;
        if (blockedUntil is not null)
        {
            // Only one caller probes an expired cooldown; a crash releases the probe after five minutes.
            probeId = Guid.NewGuid();
            command.Parameters.AddWithValue("probe", probeId.Value);
            command.CommandText = """
                update ai_invocation_cooldowns set blocked_until=now()+interval '5 minutes', probe_id=@probe, updated_at=now()
                where company_id=@company and agent_key=@agent and configuration_fingerprint=@fingerprint;
                """;
        }
        if (probeId is not null) await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AiInvocationPermit(probeId);
    }

    public async Task<AiInvocationDeferredException?> RecordFailureAsync(AiAgentRuntimeSettings settings, string? companyId,
        Exception exception, CancellationToken cancellationToken)
    {
        var failure = AiInvocationFailurePolicy.Classify(exception);
        if (!failure.BlocksFurtherInvocations) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(UpsertSql + " returning blocked_until;", connection);
        Keys(command, settings, companyId);
        command.Parameters.AddWithValue("fingerprint", Fingerprint(settings));
        command.Parameters.AddWithValue("until", DateTime.UtcNow.Add(failure.RetryDelay));
        command.Parameters.AddWithValue("reason", failure.Reason);
        // Fingerprints isolate late failures and checks from a replaced credential's cooldown.
        var retryAt = await command.ExecuteScalarAsync(cancellationToken) is DateTime saved
            ? saved : DateTime.UtcNow.Add(failure.RetryDelay);
        return new AiInvocationDeferredException(retryAt, failure.Reason, exception);
    }

    public async Task RecordSuccessAsync(AiAgentRuntimeSettings settings, string? companyId, CancellationToken cancellationToken,
        AiInvocationPermit? permit = null)
    {
        // An ordinary success must not erase a failure recorded by another invocation in flight.
        if (permit?.ProbeId is null) return;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            delete from ai_invocation_cooldowns
            where company_id=@company and agent_key=@agent and configuration_fingerprint=@fingerprint and probe_id=@probe;
            """, connection);
        Keys(command, settings, companyId);
        command.Parameters.AddWithValue("fingerprint", Fingerprint(settings));
        command.Parameters.AddWithValue("probe", permit.ProbeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string UpsertSql = """
        insert into ai_invocation_cooldowns(company_id,agent_key,configuration_fingerprint,blocked_until,reason)
        values(@company,@agent,@fingerprint,@until,@reason)
        on conflict(company_id,agent_key,configuration_fingerprint) do update
        set configuration_fingerprint=excluded.configuration_fingerprint,
            blocked_until=excluded.blocked_until,reason=excluded.reason,probe_id=null,updated_at=now()
        """;

    private static string Fingerprint(AiAgentRuntimeSettings settings) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { settings.Provider.Trim(), settings.Model.Trim(), settings.ApiKey?.Trim() }))));

    private static void Keys(NpgsqlCommand command, AiAgentRuntimeSettings settings, string? companyId)
    {
        // Guid.Empty identifies the explicit global configuration, never another tenant.
        command.Parameters.AddWithValue("company", string.IsNullOrWhiteSpace(companyId) ? Guid.Empty : Guid.Parse(companyId));
        command.Parameters.AddWithValue("agent", settings.AgentKey);
    }
}
