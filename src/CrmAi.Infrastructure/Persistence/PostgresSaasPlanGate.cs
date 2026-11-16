using Npgsql;

namespace CrmAi.Infrastructure.Persistence;

internal static class PostgresSaasPlanGate
{
    public static async Task<bool> AvailableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("select to_regprocedure('saas_plan_entitlement(uuid,text)') is not null", connection);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    public static async Task<bool> IsPaidSelfServiceAsync(NpgsqlConnection connection, Guid companyId, CancellationToken ct, NpgsqlTransaction? transaction = null)
    {
        await using var available = new NpgsqlCommand("select to_regclass('saas_self_service_subscriptions') is not null", connection, transaction);
        if (await available.ExecuteScalarAsync(ct) is not true) return false;
        await using var command = new NpgsqlCommand("select exists(select 1 from saas_self_service_subscriptions s join saas_self_service_companies g on g.company_id=s.company_id and g.enabled where s.company_id=@companyId)", connection, transaction);
        command.Parameters.AddWithValue("companyId", companyId);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    public static async Task<bool> IncludedAsync(NpgsqlConnection connection, Guid companyId, string feature, CancellationToken ct)
    {
        // Supports a rolling release and the pre-existing isolated test fixtures.
        await using var available = new NpgsqlCommand("select to_regprocedure('saas_plan_entitlement(uuid,text)') is not null", connection);
        if (await available.ExecuteScalarAsync(ct) is not true) return true;
        await using var command = new NpgsqlCommand("select coalesce(saas_plan_entitlement(@companyId,@feature),1)>0", connection);
        command.Parameters.AddWithValue("companyId", companyId);
        command.Parameters.AddWithValue("feature", feature);
        return await command.ExecuteScalarAsync(ct) is true;
    }
}
