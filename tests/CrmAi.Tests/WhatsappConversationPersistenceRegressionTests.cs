using System.Text.Json;
using CrmAi.Application;

namespace CrmAi.Tests;

public sealed class WhatsappConversationPersistenceRegressionTests
{
    [Fact]
    public void ActionStore_Should_Consolidate_Daily_Activity_And_Update_Same_Intent_Suggestion()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresWhatsappConversationActionStore.cs");

        Assert.Contains("pg_advisory_xact_lock", source);
        Assert.Contains("activity.contact_id = @contactId", source);
        Assert.Contains("activity.date_at at time zone settings.time_zone_id", source);
        Assert.Contains("update ai_agent_suggestions suggestion", source);
        Assert.Contains("status in ('pending', 'rejected')", source);
        Assert.Contains("id = @matchingSuggestionId", source);
        Assert.Contains("payload ->> 'semanticIntentKey' = @semanticIntentKey", source);
        Assert.DoesNotContain("regexp_replace(lower(trim(title))", source);
        Assert.Contains("last_analyzed_message_at = greatest(", source);
    }

    [Fact]
    public void Instagram_analysis_should_only_process_consolidated_batches_and_complete_the_checkpoint()
    {
        var pipeline = ReadSource("src/CrmAi.Application/AnalysisPipeline.cs");
        var service = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresInstagramConversationAnalysisService.cs");
        var rabbit = ReadSource("src/CrmAi.Infrastructure/RabbitMq/RabbitMqOptions.cs");

        Assert.Contains("opportunity.instagram.conversation.batch", pipeline);
        Assert.Contains("Individual message events only reset the conversation inactivity clock", pipeline);
        Assert.Contains("last_analyzed_message_at = greatest", service);
        Assert.Contains("last_analysis_status = 'completed'", service);
        Assert.Contains("Conversa Instagram analisada pelo Agent Skopos", service);
        Assert.Contains("activity.channel = @channel", service);
        Assert.Contains("activity.activity_type = @activityType", service);
        Assert.Contains("set status = 'done'", service);
        Assert.Contains("'done', @dateAt", service);
        Assert.Contains("insert into ai_agent_suggestions", service);
        Assert.Contains("agent_key = @agentKey", service);
        Assert.Contains("channel = InstagramChannel", service);
        Assert.Contains("ExistingSuggestions = semanticContext.ExistingSuggestions", service);
        Assert.DoesNotContain("'follow-up', 'instagram',\n                     'pending'", service);
        Assert.Contains("crm.events.opportunity.instagram.conversation.batch", rabbit);
        Assert.DoesNotContain("crm.events.opportunity.instagram.message.created", rabbit);
    }

    [Fact]
    public void Suggestion_context_should_be_scoped_to_the_active_channel_agent()
    {
        var repository = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresWhatsappSuggestionContextRepository.cs");

        Assert.Contains("and agent_key = @agentKey", repository);
        Assert.Contains("command.Parameters.AddWithValue(\"agentKey\", agentKey)", repository);
        Assert.DoesNotContain("and agent_key = 'whatsapp-conversation-analysis'", repository);
    }

    [Fact]
    public void Agent_Authored_Portuguese_Text_Should_Remain_Utf8()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresWhatsappConversationActionStore.cs");

        Assert.Contains("Observações comerciais:", source);
        Assert.Contains("Próximos passos:", source);
        Assert.Contains("após análise da conversa", source);
        Assert.DoesNotContain("ObservaÃ", source);
        Assert.DoesNotContain("PrÃ³ximos", source);
    }

    [Fact]
    public void Scheduled_Checkout_Should_Use_A_Distributed_Run_Lock()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/DailyCheckouts/PostgresDailyCheckoutSnapshotService.cs");

        Assert.Contains("pg_try_advisory_lock", source);
        Assert.Contains("SnapshotAlreadyGeneratedForRunAsync", source);
        Assert.Contains("pg_advisory_unlock", source);
    }

    [Fact]
    public void Semantic_Deduplication_Should_Be_Decided_By_The_OpenAi_Agent()
    {
        var client = ReadSource("src/CrmAi.Application/WhatsappConversationAnalysis/OpenAiWhatsappConversationAnalysisClient.cs");
        var schema = ReadSource("src/CrmAi.Application/WhatsappConversationAnalysis/WhatsappConversationAnalysisJsonSchema.cs");
        var contextRepository = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresWhatsappSuggestionContextRepository.cs");

        client.ShouldContainAll(
            "Deduplicacao semantica obrigatoria",
            "nao use apenas igualdade textual",
            "activityMatchingSuggestionId",
            "matchingOpenOpportunityId");
        schema.ShouldContainAll(
            "activityIntentKey",
            "opportunityIntentKey",
            "matchingOpenOpportunityId");
        contextRepository.ShouldContainAll(
            "from ai_agent_suggestions",
            "ReadOpenOpportunitiesAsync",
            "payload ->> 'semanticIntentKey'");
    }

    [Fact]
    public void WhatsappScorecard_Should_Deserialize_Large_Model_Offsets_Without_Losing_The_Analysis()
    {
        const string json = """
            {
              "conversationSummary": "Atendimento analisado.",
              "shouldCreateNote": false,
              "noteText": null,
              "shouldCreateActivity": false,
              "activityTitle": null,
              "activityNotes": null,
              "activityDueAt": null,
              "confidenceScore": 80,
              "reasons": ["Evidência localizada."],
              "scorecardItems": [{
                "criterionKey": "response_cadence",
                "score": 80,
                "confidenceScore": 80,
                "justification": "Resposta adequada.",
                "recommendation": null,
                "evidence": [{
                  "excerpt": "Equipe: retorno em seguida",
                  "participant": "Equipe",
                  "startMs": 4294967296,
                  "endMs": 4294967396,
                  "source": "transcript",
                  "confidenceScore": 80
                }]
              }]
            }
            """;

        var result = JsonSerializer.Deserialize<OpenAiWhatsappConversationAnalysisResponse>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var evidence = Assert.Single(Assert.Single(result!.ScorecardItems!).Evidence);
        Assert.Equal(4_294_967_296L, evidence.StartMs);
        Assert.Equal(4_294_967_396L, evidence.EndMs);
    }

    [Fact]
    public void SuggestionCompletionWorker_Should_Use_Contextual_Evidence_And_Persist_Alerts()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/SuggestionCompletionVerificationHostedService.cs");

        source.ShouldContainAll(
            "suggested_due_at <= now() - interval '5 minutes'",
            "from activities activity",
            "from whatsapp_messages message",
            "from instagram_messages message",
            "from notes note",
            "from opportunity_history history",
            "from meeting_audio_recordings recording",
            "verification_status = @result",
            "priority_at = case",
            "insert into notifications",
            "activity_suggestion_unfulfilled",
            "case when opportunity.id is null then 'contact' else 'opportunity' end as target_type",
            "$\"/crm/opportunities/{group.Key.TargetId}\"",
            "$\"/crm/contacts/{group.Key.TargetId}\"",
            "entityType = notification.EntityType",
            "notification.created");
    }

    [Fact]
    public void UpdatedSuggestions_Should_Reset_Previous_Verification_State()
    {
        var whatsapp = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresWhatsappConversationActionStore.cs");
        var instagram = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresInstagramConversationAnalysisService.cs");

        whatsapp.ShouldContainAll("verification_status = 'pending'", "evidence_fingerprint = null", "priority_at = null");
        instagram.ShouldContainAll("verification_status = 'pending'", "evidence_fingerprint = null", "priority_at = null");
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrmAi.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}

internal static class StringAssertionExtensions
{
    public static void ShouldContainAll(this string value, params string[] expected)
    {
        foreach (var item in expected)
        {
            Assert.Contains(item, value);
        }
    }
}
