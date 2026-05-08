using CrmAi.Domain;

namespace CrmAi.Application;

public sealed class RiskAnalysisAgent(
    IOpenAiRiskAnalysisClient openAiClient,
    RiskAnalysisAgentInputBuilder inputBuilder) : IRiskAnalysisAgent
{
    private const string AgentInstructions = """
        Voce e o Risk Analysis Agent do CRM.

        Objetivo:
        - Analisar o risco atual da oportunidade com base nos eventos, comportamento comercial e tempo de pipeline.
        - Detectar sinais de perda, classificar o nivel de risco, explicar os motivos, gerar score de risco e detectar deterioracao.

        Como calcular e interpretar metricas:
        - Use somente as metricas calculadas e as regras parametrizadas recebidas no campo commercialRuleAssessment.
        - Cada regra vem de CommercialAnalysisMetricRuleSnapshot e possui metricKey, escopo opcional de pipeline/stage, level, operator, thresholdValue e thresholdUnit.
        - A aplicacao ja converte unidades temporais para dias quando necessario e informa quais regras casaram com o valor observado.
        - Regras com escopo de stage prevalecem sobre regras de pipeline, e regras de pipeline prevalecem sobre regras globais.
        - Uma metrica sem regra aplicavel nao deve ser penalizada por limite inventado. Use-a apenas como contexto qualitativo.
        - O score final deve refletir a severidade das regras casadas, a recencia das interacoes, o historico de eventos, atividades, notas, contatos e sinais de deterioracao.

        Responsabilidades:
        - Sempre justificar o risco com motivos especificos da oportunidade.
        - Nunca gerar recomendacoes genericas; cada recomendacao deve responder a um motivo observado.
        - Considerar contexto temporal, fase atual, tempo de pipeline, atividades, notas, contatos e interacoes recentes.
        - Detectar deterioracao progressiva quando o historico indicar regressao, queda de cadencia ou acumulacao de pendencias.

        Saida:
        - Responda apenas com JSON valido no schema solicitado.
        - riskLevel deve ser LOW, MEDIUM ou HIGH.
        - riskScore deve ser inteiro de 0 a 100.
        - reasons e recommendations devem ser arrays em portugues claro.
        """;

    public async Task<RiskAnalysisResult> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken)
    {
        var request = inputBuilder.Build(context);
        var agentResponse = await openAiClient.AnalyzeAsync(AgentInstructions, request.Input, cancellationToken);
        var riskScore = Math.Clamp(agentResponse.RiskScore, 0, 100);

        return new RiskAnalysisResult(
            ParseRiskLevel(agentResponse.RiskLevel, riskScore),
            riskScore,
            Clean(agentResponse.Reasons),
            Clean(agentResponse.Recommendations),
            request.SnapshotUpdate);
    }

    private static RiskLevel ParseRiskLevel(string riskLevel, int riskScore)
        => riskLevel.ToUpperInvariant() switch
        {
            "HIGH" => RiskLevel.High,
            "MEDIUM" => RiskLevel.Medium,
            "LOW" => RiskLevel.Low,
            _ => riskScore >= 70 ? RiskLevel.High : riskScore >= 40 ? RiskLevel.Medium : RiskLevel.Low
        };

    private static IReadOnlyCollection<string> Clean(IReadOnlyCollection<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
