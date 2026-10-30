using System.Reflection;
using CrmAi.Infrastructure.Persistence;
using Npgsql;

namespace CrmAi.Tests;

public sealed class CoexistenceHistoryDatabaseFact : FactAttribute
{
    public CoexistenceHistoryDatabaseFact()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_COEX_TEST_DATABASE")))
            Skip = "Requires an explicit disposable localhost *_test AI_COEX_TEST_DATABASE.";
    }
}

public sealed class WhatsappCoexistenceHistoryTests
{
    [CoexistenceHistoryDatabaseFact]
    public async Task Imported_messages_cannot_reopen_response_reminders_or_enter_automatic_transcripts()
    {
        var cs = Environment.GetEnvironmentVariable("AI_COEX_TEST_DATABASE")!;
        var settings = new NpgsqlConnectionStringBuilder(cs);
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1" });
        Assert.EndsWith("_test", settings.Database);
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        var schema = "coex_ai_" + Guid.NewGuid().ToString("N");
        await new NpgsqlCommand($"create schema {schema}; set search_path to {schema};", connection).ExecuteNonQueryAsync();
        try
        {
            await new NpgsqlCommand("""
                create table whatsapp_messages(id uuid,conversation_id uuid,direction text,message_at timestamptz,created_at timestamptz,status text,is_historical boolean,message_type text,text text,sender_name text);
                create table whatsapp_conversations(id uuid,instance_id uuid,contact_name text);
                create table whatsapp_instances(id uuid,name text);
                create table whatsapp_message_audio_transcriptions(whatsapp_message_id uuid,transcript text,status text,updated_at timestamptz);
                """, connection).ExecuteNonQueryAsync();
            var conversation = Guid.NewGuid();
            await using var seed = new NpgsqlCommand("""
                insert into whatsapp_conversations values(@Conversation,gen_random_uuid(),'Cliente de teste');
                insert into whatsapp_messages values
                    (gen_random_uuid(),@Conversation,'outgoing',now()-interval '1 minute',now(),'sent',false,'text','resposta ao vivo',null),
                    (gen_random_uuid(),@Conversation,'incoming',now(),now(),'received',true,'text','histórico importado',null);
                """, connection);
            seed.Parameters.AddWithValue("Conversation", conversation);
            await seed.ExecuteNonQueryAsync();
            var latest = typeof(PostgresWhatsappConversationActionStore).GetMethod("ReadLatestIncomingMessageAtAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Null(await (Task<DateTime?>)latest.Invoke(null, new object[] { connection, conversation, CancellationToken.None })!);
            var transcript = typeof(PostgresWhatsappConversationAnalysisScheduler).GetMethod("ReadTranscriptAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = await (Task<string>)transcript.Invoke(null, new object?[] { connection, conversation.ToString(), null, CancellationToken.None })!;
            Assert.Contains("resposta ao vivo", result);
            Assert.DoesNotContain("histórico importado", result);
            // Readers can be deployed before the shared schema: legacy rows remain eligible.
            await new NpgsqlCommand("alter table whatsapp_messages drop column is_historical", connection).ExecuteNonQueryAsync();
            Assert.NotNull(await (Task<DateTime?>)latest.Invoke(null, new object[] { connection, conversation, CancellationToken.None })!);
        }
        finally
        {
            await new NpgsqlCommand($"drop schema {schema} cascade;", connection).ExecuteNonQueryAsync();
        }
    }
}
