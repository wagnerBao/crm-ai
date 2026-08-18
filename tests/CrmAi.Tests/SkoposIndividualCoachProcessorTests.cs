using CrmAi.Infrastructure.SkoposCoach;

namespace CrmAi.Tests;

public sealed class SkoposIndividualCoachProcessorTests
{
    [Fact]
    public void Coverage_reaches_full_score_only_with_complete_sample()
    {
        var metrics = new SkoposIndividualCoachProcessor.ObjectiveMetrics(
            ReportCount: 10,
            OpportunityCount: 3,
            SourceCount: 2,
            ResponseCount: 8,
            MedianResponseSeconds: 300,
            P90ResponseSeconds: 1800,
            ActivityCount: 12,
            CompletedActivityCount: 10,
            OverdueActivityCount: 1);

        Assert.Equal(100, SkoposIndividualCoachProcessor.Coverage(metrics));
        Assert.Equal(95, SkoposIndividualCoachProcessor.Confidence(metrics));
    }

    [Fact]
    public void Coverage_exposes_missing_response_attribution()
    {
        var metrics = new SkoposIndividualCoachProcessor.ObjectiveMetrics(
            ReportCount: 5,
            OpportunityCount: 3,
            SourceCount: 1,
            ResponseCount: 0,
            MedianResponseSeconds: null,
            P90ResponseSeconds: null,
            ActivityCount: 0,
            CompletedActivityCount: 0,
            OverdueActivityCount: 0);

        Assert.Equal(60, SkoposIndividualCoachProcessor.Coverage(metrics));
        Assert.InRange(SkoposIndividualCoachProcessor.Confidence(metrics), 0, 94);
    }

    [Theory]
    [InlineData("opening_connection", "Abertura e conexão", "service")]
    [InlineData("qualification", "Qualificação", "qualification")]
    [InlineData("needs_discovery", "Descoberta de necessidade", "qualification")]
    [InlineData("decision_process", "Decisor e processo de compra", "qualification")]
    [InlineData("value_building", "Construção de valor", "proposal")]
    [InlineData("objection_handling", "Tratamento de objeções", "objections")]
    [InlineData("proposal_clarity", "Clareza de proposta", "proposal")]
    [InlineData("negotiation", "Negociação", "proposal")]
    [InlineData("next_step", "Definição de próximo passo", "cadence")]
    [InlineData("playbook_adherence", "Aderência ao playbook", "execution")]
    [InlineData("response_time", "Tempo de resposta", "service")]
    [InlineData("product_knowledge", "Domínio de produto", "product")]
    public void Scorecard_criteria_map_to_the_current_pdi_competency_catalog(string key, string title, string expected)
    {
        Assert.Equal(expected, SkoposIndividualCoachProcessor.MapCriterionToCompetency(key, title));
    }

    [Fact]
    public void Reviewed_scorecard_evidence_has_priority_in_the_weighted_baseline()
    {
        var score = SkoposIndividualCoachProcessor.ScoreStructured(
        [
            new(20, 100, 10, Reviewed: false),
            new(80, 100, 10, Reviewed: true)
        ]);

        Assert.Equal(60, score);
    }

    [Fact]
    public void Structured_score_ignores_uncovered_items_and_respects_confidence()
    {
        var score = SkoposIndividualCoachProcessor.ScoreStructured(
        [
            new(10, 0, 10, Reviewed: true),
            new(40, 50, 10, Reviewed: false),
            new(80, 100, 10, Reviewed: false)
        ]);

        Assert.Equal(67, score);
    }

    [Fact]
    public void Unknown_custom_scorecard_criterion_does_not_influence_the_pdi()
    {
        Assert.Null(SkoposIndividualCoachProcessor.MapCriterionToCompetency("custom_criterion", "Critério exclusivo"));
    }
}
