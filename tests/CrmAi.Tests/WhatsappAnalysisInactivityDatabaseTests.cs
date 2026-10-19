using System.Collections;
using System.Reflection;
using CrmAi.Infrastructure.Persistence;
using Npgsql;

namespace CrmAi.Tests;

public sealed class WhatsappInactivityPostgresFactAttribute : FactAttribute
{
    public WhatsappInactivityPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONVERSATION_ANALYSIS_TEST_DATABASE")))
            Skip = "Requires local PostgreSQL; uses session-local temporary tables only.";
    }
}

public sealed class WhatsappAnalysisInactivityDatabaseTests : IAsyncLifetime
{
    private NpgsqlConnection? connection;

    public async Task InitializeAsync()
    {
        var value = Environment.GetEnvironmentVariable("CONVERSATION_ANALYSIS_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(value)) return;
        var settings = new NpgsqlConnectionStringBuilder(value);
        if (settings.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Inactivity fixtures require local PostgreSQL.");
        settings.SearchPath = "pg_temp";
        settings.Pooling = false;
        connection = new(settings.ConnectionString);
        await connection.OpenAsync();
        await Sql("""
            create temporary table opportunities(id uuid, company_id uuid);
            create temporary table whatsapp_conversations(id uuid, company_id uuid, last_message_at timestamptz);
            create temporary table whatsapp_conversation_activity_settings(company_id uuid, is_active boolean, inactivity_minutes integer);
            create temporary table ai_agent_settings(agent_key text, company_id uuid, is_active boolean, debounce_minutes integer, updated_at timestamptz);
            create temporary table ai_insights(id uuid, opportunity_id uuid, kind text, status text, message text, updated_at timestamptz);
            set default_transaction_read_only=on;
            insert into opportunities values('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002');
            insert into whatsapp_conversations values('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002',now()-interval '15 minutes');
            insert into whatsapp_conversation_activity_settings values('00000000-0000-0000-0000-000000000002',true,20);
            insert into ai_agent_settings values('whatsapp-conversation-analysis','00000000-0000-0000-0000-000000000002',true,10,now());
            insert into ai_insights values(gen_random_uuid(),'00000000-0000-0000-0000-000000000001','whatsapp-conversation-analysis','pending',
                '{"conversationId":"00000000-0000-0000-0000-000000000003"}',now()-interval '2 hours');
            """);
    }

    public async Task DisposeAsync()
    {
        if (connection is not null) await connection.DisposeAsync();
    }

    [WhatsappInactivityPostgresFact]
    public async Task Legacy_opportunity_scheduler_obeys_activity_inactivity_and_latest_conversation_message()
    {
        Assert.Empty(await Due());
        await Sql("update whatsapp_conversations set last_message_at=now()-interval '21 minutes';");
        Assert.Single(await Due());
        await Sql("update whatsapp_conversations set last_message_at=now()-interval '11 seconds';");
        Assert.Empty(await Due());
    }

    [WhatsappInactivityPostgresFact]
    public async Task Global_activity_setting_precedes_company_agent_delay()
    {
        await Sql("update whatsapp_conversation_activity_settings set company_id=null;");
        Assert.Empty(await Due());
        await Sql("update whatsapp_conversations set last_message_at=now()-interval '21 minutes';");
        Assert.Single(await Due());
    }

    [WhatsappInactivityPostgresFact]
    public async Task Disabled_activity_does_not_persist_through_the_legacy_scheduler()
    {
        await Sql("update whatsapp_conversations set last_message_at=now()-interval '2 hours'; update whatsapp_conversation_activity_settings set is_active=false;");
        Assert.Empty(await Due());
    }

    [WhatsappInactivityPostgresFact]
    public async Task Agent_delay_remains_the_fallback_and_deferred_provider_retry_is_respected()
    {
        await Sql("delete from whatsapp_conversation_activity_settings;");
        Assert.Single(await Due());
        await Sql("update ai_insights set message=jsonb_set(message::jsonb,'{nextAttemptUtc}',to_jsonb(now()+interval '1 hour'))::text;");
        Assert.Empty(await Due());
    }

    private async Task<object[]> Due()
    {
        var method = typeof(PostgresWhatsappConversationAnalysisScheduler).GetMethod("ReadDuePendingAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, [connection!, 20, 10, CancellationToken.None])!;
        await task;
        return ((IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!).Cast<object>().ToArray();
    }

    private async Task Sql(string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
