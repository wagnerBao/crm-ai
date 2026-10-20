using CrmAi.Infrastructure.SkoposCoach;
using System.Reflection;
using System.Text.Json;

namespace CrmAi.Tests;

public sealed class SkoposIndividualCoachProcessorTests
{
    [Fact]
    public void Qualitative_context_cannot_generate_a_numeric_grade()
    {
        Assert.Null(SkoposIndividualCoachProcessor.ScoreFromEvidence([]));
        Assert.Null(SkoposIndividualCoachProcessor.ScoreFromEvidence([new(90, 0, 1, false)]));
        Assert.Equal(90, SkoposIndividualCoachProcessor.ScoreFromEvidence([new(90, 100, 1, true)]));
    }

    [Fact]
    public void Explicit_competency_mapping_overrides_name_aliases_and_can_disable_a_link()
    {
        Assert.Equal("product", SkoposIndividualCoachProcessor.MapCriterionToCompetency("next_step", "Critério próprio", "product"));
        Assert.Null(SkoposIndividualCoachProcessor.MapCriterionToCompetency("next_step", "Próximo passo", "none"));
        Assert.Null(SkoposIndividualCoachProcessor.MapCriterionToCompetency("next_step", "Próximo passo", "invalid"));
    }

    [Fact]
    public void Development_priority_rejects_evidence_from_another_competency_or_outside_the_sample()
    {
        var valid = Guid.NewGuid(); var otherCompetency = Guid.NewGuid(); var outsideSample = Guid.NewGuid();
        var allowed = new[] { valid.ToString(), otherCompetency.ToString() }.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidence = SkoposIndividualCoachProcessor.SelectCompetencyEvidence(
            [otherCompetency.ToString(), outsideSample.ToString()], [valid, outsideSample], allowed);
        Assert.Equal([valid.ToString()], evidence);
    }

    [Fact]
    public void Development_guide_fallback_provides_practice_deliverables_and_manager_review()
    {
        var guide = SkoposIndividualCoachProcessor.NormalizeGuide(null, "cadence", 4, 30);
        Assert.Contains("4 relatório(s)", guide.Diagnosis);
        Assert.Contains("próximo passo", guide.FirstAction);
        Assert.Equal(3, guide.Steps.Count);
        Assert.All(guide.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Deliverable)));
        Assert.Equal(30, guide.Steps.Last().DueInDays);
        Assert.Contains("revisão semanal", guide.ManagerSupport);
    }

    [Fact]
    public void Development_steps_are_ordered_and_limited_to_the_item_horizon()
    {
        var input = new IndividualDevelopmentGuide("Achado", "Impacto possível", "Ação", "Revisar casos", "Medir", [
            new("Verificar", "Revisar resultado", "Revisão registrada", 90),
            new("Praticar", "Aplicar no atendimento", "Caso registrado", 0),
            new("Inválida", "", "", 4)
        ]);
        var guide = SkoposIndividualCoachProcessor.NormalizeGuide(input, "qualification", 3, 14);
        Assert.Equal(new[] { 1, 14 }, guide.Steps.Select(step => step.DueInDays));
        Assert.Equal("Praticar", guide.Steps.First().Title);
    }

    [Fact]
    public void Incomplete_development_steps_get_a_complete_fallback_without_losing_the_diagnosis()
    {
        var input = new IndividualDevelopmentGuide("Achado confirmado", "", "", "", "", []);
        var guide = SkoposIndividualCoachProcessor.NormalizeGuide(input, "objections", 2, 7);
        Assert.Equal("Achado confirmado", guide.Diagnosis);
        Assert.Equal(3, guide.Steps.Count);
        Assert.All(guide.Steps, step => Assert.InRange(step.DueInDays, 1, 7));
        Assert.False(string.IsNullOrWhiteSpace(guide.SuccessCriteria));
    }

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

    [Fact]
    public void Individual_coach_response_schema_does_not_serialize_reused_definitions_as_null()
    {
        var field = typeof(SkoposIndividualCoachClient).GetField("Schema", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(field.GetValue(null)));
        var properties = document.RootElement.GetProperty("properties");
        var strengthProperties = properties.GetProperty("strengths").GetProperty("items").GetProperty("properties");
        var itemProperties = properties.GetProperty("items").GetProperty("items").GetProperty("properties");

        Assert.Equal("string", strengthProperties.GetProperty("key").GetProperty("type").GetString());
        Assert.Equal("integer", strengthProperties.GetProperty("score").GetProperty("type").GetString());
        Assert.Equal("string", itemProperties.GetProperty("competencyKey").GetProperty("type").GetString());
        Assert.Equal("string", itemProperties.GetProperty("action").GetProperty("type").GetString());
    }
}
