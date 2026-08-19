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
