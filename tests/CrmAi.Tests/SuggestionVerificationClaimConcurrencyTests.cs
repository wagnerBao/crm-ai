using System.Reflection;
using CrmAi.Application;
using CrmAi.Infrastructure.Persistence;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CrmAi.Tests;

/// <summary>Real row locks across sessions, restricted to an explicit local *_test database.</summary>
public sealed class SuggestionVerificationClaimConcurrencyTests : IAsyncLifetime
{
    private readonly string schema = "suggestion_claim_" + Guid.NewGuid().ToString("N");
    private NpgsqlDataSource? admin;
    private NpgsqlDataSource? db;
    private SuggestionCompletionVerificationProcessor worker = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("SUGGESTION_VERIFICATION_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var configuration = new NpgsqlConnectionStringBuilder(connectionString);
        if (configuration.Host is not ("127.0.0.1" or "localhost" or "::1") ||
            configuration.Database?.EndsWith("_test", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Claim concurrency fixtures require an explicit local *_test database.");
        configuration.SearchPath = schema;
        configuration.MaxPoolSize = 6;
        configuration.NoResetOnClose = false;
        configuration.ApplicationName = "Skopos.Verification.Claim.Tests";
        admin = NpgsqlDataSource.Create(configuration.ConnectionString);
        await using (var createSchema = admin.CreateCommand($"create schema {schema}"))
            await createSchema.ExecuteNonQueryAsync();
        db = NpgsqlDataSource.Create(configuration.ConnectionString);
        await Sql(SuggestionVerificationInteractionTests.CreateTablesSql.Replace("create temporary table", "create table", StringComparison.Ordinal));
        // Claiming never loads settings, calls a provider, or publishes messages.
        worker = new(db, null!, null!, Options.Create(new RabbitMqOptions()),
            NullLogger<SuggestionCompletionVerificationProcessor>.Instance,
            Options.Create(new OpenAiRiskAnalysisOptions()), null!);
    }

    public async Task DisposeAsync()
    {
        if (db is not null) await db.DisposeAsync();
        if (admin is not null)
        {
            try
            {
                await using var command = admin.CreateCommand($"drop schema if exists {schema} cascade");
                await command.ExecuteNonQueryAsync();
            }
            finally { await admin.DisposeAsync(); }
        }
    }

    [SuggestionPostgresFact]
    public async Task Three_replicas_cannot_claim_the_same_suggestion_and_leave_no_extra_attempts()
    {
        await Sql("""
            insert into ai_agent_suggestions(id,company_id,contact_id)
            select gen_random_uuid(),gen_random_uuid(),gen_random_uuid() from generate_series(1,63)
            """);
        var claimed = new HashSet<Guid>();
        for (var cycle = 0; cycle < 64 && claimed.Count < 63; cycle++)
        {
            var results = await Task.WhenAll(Claim(), Claim(), Claim());
            foreach (var id in results.OfType<Guid>()) Assert.True(claimed.Add(id), "A replica reclaimed an active lease.");
        }
        Assert.Equal(63, claimed.Count);
        Assert.Null(await Claim());
        Assert.Equal(63L, await Scalar<long>("select count(*) from ai_agent_suggestions where verification_status='processing' and verification_attempt_count=1"));
        Assert.Equal(0L, await Scalar<long>("select count(*) from ai_agent_suggestion_verifications"));
    }

    [SuggestionPostgresFact]
    public async Task A_locked_first_row_does_not_block_another_replica()
    {
        var locked = Guid.NewGuid();
        var available = Guid.NewGuid();
        await Sql($"""
            insert into ai_agent_suggestions(id,company_id,contact_id,created_at)
            values ('{locked}',gen_random_uuid(),gen_random_uuid(),now()-interval '3 days'),
                   ('{available}',gen_random_uuid(),gen_random_uuid(),now()-interval '2 days')
            """);
        await using var connection = await db!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand($"select id from ai_agent_suggestions where id='{locked}' for update", connection, transaction);
            await command.ExecuteScalarAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(available, await Claim(timeout.Token));
            Assert.Null(await Claim(timeout.Token));
        }
        finally { await transaction.RollbackAsync(); }
        Assert.Equal(locked, await Claim());
    }

    [SuggestionPostgresFact]
    public async Task Claim_preserves_priority_order_and_active_leases_while_recovering_expired_work()
    {
        var expired = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var prioritized = Guid.NewGuid();
        var active = Guid.NewGuid();
        await Sql($"""
            insert into ai_agent_suggestions(id,company_id,contact_id,created_at,verification_status,updated_at,next_verification_at,priority_at)
            values ('{active}',gen_random_uuid(),gen_random_uuid(),now()-interval '5 days','processing',now(),now()-interval '1 second',null),
                   ('{prioritized}',gen_random_uuid(),gen_random_uuid(),now()-interval '4 days','pending',now(),null,now()),
                   ('{expired}',gen_random_uuid(),gen_random_uuid(),now()-interval '3 days','processing',now()-interval '16 minutes',now()-interval '3 days',null),
                   ('{pending}',gen_random_uuid(),gen_random_uuid(),now()-interval '2 days','pending',now(),null,null)
            """);
        Assert.Equal(expired, await Claim());
        Assert.Equal(pending, await Claim());
        Assert.Equal(prioritized, await Claim());
        Assert.Null(await Claim());
        Assert.Equal(0, await Scalar<int>($"select verification_attempt_count from ai_agent_suggestions where id='{active}'"));
        Assert.Equal(1L, await Scalar<long>("select count(*) from ai_agent_suggestions where priority_at is not null"));
    }

    private async Task<Guid?> Claim(CancellationToken cancellationToken = default)
    {
        var method = typeof(SuggestionCompletionVerificationProcessor).GetMethod("ClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(worker, [cancellationToken])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        return result is null ? null : (Guid)result.GetType().GetProperty("Id")!.GetValue(result)!;
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
}
