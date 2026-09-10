using CrmAi.Application;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresRiskAnalysisState(NpgsqlDataSource dataSource) : IRiskAnalysisState
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var stream = typeof(PostgresRiskAnalysisState).Assembly.GetManifestResourceStream("RiskAnalysisState.sql")
            ?? throw new InvalidOperationException("Risk analysis state schema is missing.");
        using var reader = new StreamReader(stream);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("select pg_advisory_xact_lock(609092027);" + await reader.ReadToEndAsync(ct), connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<RiskAnalysisWork?> RegisterAsync(RiskAnalysisRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            insert into risk_analysis_state(company_id, opportunity_id) values(@company,@opportunity) on conflict do nothing;
            select revision, due_at from risk_analysis_state
            where company_id=@company and opportunity_id=@opportunity for update;
            """, connection, transaction);
        Keys(command, request.CompanyId, request.OpportunityId);
        long current;
        DateTime due;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            current = reader.GetInt64(0); due = reader.GetDateTime(1);
        }
        command.CommandText = "select revision from risk_analysis_event_receipts where company_id=@company and opportunity_id=@opportunity and event_id=@event";
        command.Parameters.AddWithValue("event", request.Event.EventId);
        var received = await command.ExecuteScalarAsync(ct);
        long revision;
        if (received is long previous)
        {
            if (previous != current) { await transaction.CommitAsync(ct); return null; }
            revision = previous;
        }
        else
        {
            command.CommandText = """
                update risk_analysis_state set revision=revision+1, due_at=now()+make_interval(mins=>@minutes), attempts=0,last_error=null
                where company_id=@company and opportunity_id=@opportunity returning revision,due_at;
                """;
            command.Parameters.AddWithValue("minutes", Math.Clamp(request.DelayMinutes, 1, 1440));
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct); revision = reader.GetInt64(0); due = reader.GetDateTime(1);
            }
            command.CommandText = "insert into risk_analysis_event_receipts(company_id,opportunity_id,event_id,revision) values(@company,@opportunity,@event,@revision)";
            command.Parameters.AddWithValue("revision", revision);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(request.CompanyId, request.OpportunityId, revision, request.Event, due);
    }

    public async Task<RiskClaimResult> AcquireAsync(RiskAnalysisWork work, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            select revision, processed_revision, due_at, lease_until, attempts, now()
            from risk_analysis_state where company_id=@company and opportunity_id=@opportunity for update;
            """, connection, transaction);
        Keys(command, work.CompanyId, work.OpportunityId);
        DateTime due, now;
        DateTime? leaseUntil;
        long revision, processed;
        int attempts;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return new(RiskClaimStatus.Obsolete);
            revision=reader.GetInt64(0); processed=reader.GetInt64(1); due=reader.GetDateTime(2);
            leaseUntil=reader.IsDBNull(3) ? null : reader.GetDateTime(3); attempts=reader.GetInt32(4); now=reader.GetDateTime(5);
        }
        if (revision != work.Revision || processed >= work.Revision) return new(RiskClaimStatus.Obsolete);
        if (due > now) return new(RiskClaimStatus.Wait, RetryAtUtc: due);
        if (leaseUntil > now) return new(RiskClaimStatus.Wait, RetryAtUtc: now.AddSeconds(10));
        if (attempts >= 3) return new(RiskClaimStatus.Exhausted);
        var lease = Guid.NewGuid();
        command.CommandText = """
            update risk_analysis_state set lease_id=@lease,lease_until=now()+interval '10 minutes',attempts=attempts+1
            where company_id=@company and opportunity_id=@opportunity;
            """;
        command.Parameters.AddWithValue("lease",lease);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(RiskClaimStatus.Acquired,new(work.CompanyId,work.OpportunityId,lease,work.Revision,work.Event),Attempts: attempts+1);
    }

    public Task CompleteAsync(RiskAnalysisClaim claim, string? error, CancellationToken ct) => FinishAsync(claim,error,false,ct);
    public Task RetryAsync(RiskAnalysisClaim claim, string error, CancellationToken ct) => FinishAsync(claim,error,true,ct);
    private async Task FinishAsync(RiskAnalysisClaim claim, string? error, bool retry, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            update risk_analysis_state
            set processed_revision=case when @retry then processed_revision else greatest(processed_revision,@revision) end,
                due_at=case when @retry and revision=@revision then now()+interval '5 minutes' else due_at end,
                lease_id=null,lease_until=null,last_error=case when revision=@revision then @error else last_error end
            where company_id=@company and opportunity_id=@opportunity and lease_id=@lease;
            """,connection);
        Keys(command,claim.CompanyId,claim.OpportunityId);
        command.Parameters.AddWithValue("lease",claim.LeaseId);
        command.Parameters.AddWithValue("revision",claim.Revision);
        command.Parameters.AddWithValue("retry",retry);
        command.Parameters.AddWithValue("error",NpgsqlDbType.Text,(object?)(error is null ? null : error[..Math.Min(error.Length,2000)]) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static void Keys(NpgsqlCommand command,Guid company,Guid opportunity)
    {
        command.Parameters.AddWithValue("company",company);
        command.Parameters.AddWithValue("opportunity",opportunity);
    }
}
