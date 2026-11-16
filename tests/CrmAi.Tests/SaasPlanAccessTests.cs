using CrmAi.Application;
using CrmAi.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CrmAi.Tests;

public sealed class SaasPlanPostgresFactAttribute : FactAttribute
{
    public SaasPlanPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SAAS_LIMITS_TEST_CONNECTION_STRING"))) Skip = "Requires disposable local database ending in _test.";
    }
}

public sealed class SaasPlanAccessTests
{
    [SaasPlanPostgresFact]
    public async Task Shadow_mode_does_not_unlock_basic_ai_or_agents_intelligence_and_other_tenants_are_unchanged()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SAAS_LIMITS_TEST_CONNECTION_STRING"));
        if (builder.Host is not ("127.0.0.1" or "localhost") || !builder.Database!.EndsWith("_test", StringComparison.Ordinal)) throw new InvalidOperationException("Only a disposable local test database is permitted.");
        var schema = "ai_plan_" + Guid.NewGuid().ToString("N");
        var basic = Guid.NewGuid(); var agents = Guid.NewGuid();
        await using var admin = new NpgsqlConnection(builder.ConnectionString); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"create schema {schema}", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            builder.SearchPath = schema;
            await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
            await using (var setup = source.CreateCommand($"""
                create function saas_plan_entitlement(p_company uuid,p_key text) returns numeric language sql as $$
                select case when p_company='{basic}'::uuid then 0 when p_company='{agents}'::uuid then case when p_key='feature_intelligence' then 0 else 1 end else null end::numeric $$;
                """)) await setup.ExecuteNonQueryAsync();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Saas:AiCreditMeteringEnabled"]="false" }).Build();
            var store = new PostgresAiAgentInvocationLogStore(source, configuration);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureCreditsAvailableAsync(new("whatsapp", CompanyId: basic.ToString()), default));
            await store.EnsureCreditsAvailableAsync(new("whatsapp", CompanyId: agents.ToString()), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureCreditsAvailableAsync(new("skopos-individual-coach", CompanyId: agents.ToString()), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureCreditsAvailableAsync(new("daily-checkout", CompanyId: agents.ToString()), default));
            await store.EnsureCreditsAvailableAsync(new("skopos-coach", CompanyId: Guid.NewGuid().ToString()), default);
            await using (var setup = source.CreateCommand($"""
                create table saas_self_service_subscriptions(company_id uuid primary key);
                create table saas_self_service_companies(company_id uuid primary key,enabled boolean);
                create table saas_contracts(company_id uuid,status text);
                create table ai_credit_reservations(company_id uuid,invocation_id text,reserved_credits bigint,status text,expires_at timestamptz,updated_at timestamptz);
                create table ai_credit_lots(id uuid default gen_random_uuid(),company_id uuid,source_type text,remaining_credits bigint,expires_at timestamptz,created_at timestamptz default now());
                create table ai_credit_ledger_entries(company_id uuid,entry_type text,credits bigint,description text,invocation_id text);
                create unique index uq_test_ledger on ai_credit_ledger_entries(company_id,invocation_id,entry_type) where invocation_id is not null;
                create table ai_rate_cards(provider text,model_pattern text,input_credits_per_thousand numeric,output_credits_per_thousand numeric,cached_credits_per_thousand numeric,effective_from timestamptz,effective_to timestamptz);
                create table ai_usage_events(company_id uuid,invocation_id text,provider text,model text,operation text,input_tokens bigint,output_tokens bigint,cached_input_tokens bigint,calculated_credits bigint,metering_mode text,unique(company_id,invocation_id));
                insert into saas_self_service_subscriptions values('{agents}');
                insert into saas_self_service_companies values('{agents}',true);
                insert into saas_contracts values('{agents}','active');
                insert into ai_credit_lots(company_id,source_type,remaining_credits) values('{agents}','allowance',200);
                insert into ai_rate_cards values('openai','*',1,1,1,now()-interval '1 day',null);
                """)) await setup.ExecuteNonQueryAsync();
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName,"CrmAi.sln"))) root = root.Parent;
            await using (var logs = source.CreateCommand(await File.ReadAllTextAsync(Path.Combine(root!.FullName,"database/20260623_create_ai_agent_invocation_logs.sql")))) await logs.ExecuteNonQueryAsync();
            var paidContext = new AiAgentInvocationContext("whatsapp", CompanyId: agents.ToString());
            await store.EnsureCreditsAvailableAsync(paidContext, default);
            await using (var reserved = source.CreateCommand("select sum(reserved_credits)::bigint from ai_credit_reservations where status='reserved'")) Assert.Equal(100L, await reserved.ExecuteScalarAsync());
            var now = DateTime.UtcNow;
            await store.SaveAsync(new(Guid.NewGuid(),"test","openai","test","test","whatsapp","/test",200,true,"success","{}",null,null,null,null,new(1000,0,1000,0,0),paidContext,now,now),default);
            await using (var balance = source.CreateCommand("select sum(remaining_credits)::bigint from ai_credit_lots")) Assert.Equal(199L, await balance.ExecuteScalarAsync());
            await using (var settled = source.CreateCommand("select status from ai_credit_reservations")) Assert.Equal("settled", await settled.ExecuteScalarAsync());
            await using (var mode = source.CreateCommand("select metering_mode from ai_usage_events")) Assert.Equal("enforced", await mode.ExecuteScalarAsync());

        }
        finally { await using var drop = new NpgsqlCommand($"drop schema {schema} cascade", admin); await drop.ExecuteNonQueryAsync(); }
    }
}
