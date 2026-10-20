using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrmAi.Application;
using Microsoft.Extensions.Options;

namespace CrmAi.Infrastructure.SkoposCoach;

public sealed class SkoposIndividualCoachClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore)
{
    public const string DevelopmentInstructions = """
        Plano de desenvolvimento individual:
        - Use as competencias e evidencias ja analisadas. Priorize ate tres pontos com maior necessidade e suporte factual.
        - A linha de base e a nota deterministica fornecida; se for null, baselineScore e targetScore devem ser null. Nao gere nota por frequencia de palavras. Nao compare pessoas entre si.
        - Explique o comportamento observado e por que a prioridade merece atencao, sem diagnosticar personalidade ou intencao.
        - Cada item deve conter guide: diagnosis (achado com evidencia), whyItMatters (impacto possivel, nao causalidade provada),
          firstAction (acao que a pessoa pode iniciar), managerSupport (apoio concreto do gestor),
          successCriteria (resultado observavel e forma de revisar) e steps (duas a quatro etapas em ordem de execucao).
        - Cada etapa inclui title, instruction, deliverable e dueInDays relativo ao inicio proposto do ciclo.
          Combine revisao de casos, pratica aplicada e verificacao do resultado. O prazo da etapa nao pode exceder dueInDays do item.
        - Consulte evidenceSample para os exemplos e relacione evidenceIds somente aos exemplos cuja competencyKeys inclui a competencia do item. Nao use um exemplo como prova de recorrencia.
        - action resume o habito a desenvolver; measurement define numerador, denominador ou amostra de revisao quando pertinente.
        - resource deve usar os materiais e produtos existentes no contexto ou sugerir apoio do gestor, sem inventar cursos ou links.
        - Se houve plano anterior, use-o como historico para continuidade, sem afirmar melhora quando os periodos ou dados nao permitem.
        - Contatos sem oportunidade fazem parte do atendimento. Sem oportunidade vinculada nao significa oportunidade perdida nem falha do colaborador. Nao invente motivos de nao conversao.
        - Separe fatos observados, hipoteses para revisao e ausencia de cobertura. Reconheca boas praticas e problemas com exemplos, respeitando os denominadores e o escopo de cada metrica.
        - serviceSegments separa as evidencias pelo vinculo informado na analise; compare apenas competencias cobertas, com denominadores e sem atribuir causalidade.
        - Se evidenceTextCompactedForBudget estiver ativo, os trechos foram encurtados para respeitar o orcamento; reconheca quando um exemplo curto nao permite concluir o comportamento.
        - As contagens de carteira usam vinculos atuais; nao as apresente como conversao de uma coorte nem como autoria historica comprovada. Casos sem analise nao receberam nota.
        - Metas individuais do check-in nao sao configuracao operacional do checkout. Zero sem meta aplicavel significa nao aplicavel, nunca desconfiguracao.
        - Metas sao propostas para revisao humana. Ausencia de resposta, meta ou scorecard deve ser reconhecida como limitacao.
        """;
    internal const int MaximumInputCharacters = 32_000;
    internal static string SerializeBoundedInput(object input)
    {
        var json = JsonSerializer.Serialize(input, JsonOptions);
        if (json.Length <= MaximumInputCharacters) return json;
        var root = JsonNode.Parse(json) as JsonObject;
        if (root?["evidenceSample"] is JsonArray examples)
        {
            // Keep every selected case, its membership and scores; compact narrative only.
            foreach (var limits in new (int Summary, int Observation)[] { (240, 90), (180, 60), (120, 40), (80, 25) })
            {
                foreach (var example in examples.OfType<JsonObject>())
                {
                    Trim(example, "summary", limits.Summary);
                    if (example["criteria"] is JsonArray criteria)
                        foreach (var criterion in criteria.OfType<JsonObject>()) Trim(criterion, "observation", limits.Observation);
                }
                root["evidenceTextCompactedForBudget"] = true;
                json = root.ToJsonString(JsonOptions);
                if (json.Length <= MaximumInputCharacters) return json;
            }
        }
        throw new SkoposContextBudgetException();

        static void Trim(JsonObject item, string key, int limit)
        {
            if (item[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > limit)
                item[key] = text[..limit];
        }
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IndividualCoachResult?> AnalyzeAsync(AiAgentRuntimeSettings settings, object input, string companyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) return null;
        var endpoint = options.Value.ResponsesEndpoint;
        var model = string.IsNullOrWhiteSpace(settings.Model) ? options.Value.Model : settings.Model;
        var payload = new
        {
            model,
            reasoning = new { effort = "low" },
            instructions = settings.Instructions,
            input = SerializeBoundedInput(input),
            store = false,
            text = new { format = new { type = "json_schema", name = "skopos_individual_pdi", strict = true, schema = Schema } }
        };
        var requestJson = JsonSerializer.Serialize(payload, JsonOptions);
        var startedAt = DateTime.UtcNow;
        string? responseBody = null;
        HttpResponseMessage? response = null;
        try
        {
            await invocationLogStore.EnsureCreditsAvailableAsync(new AiAgentInvocationContext("skopos-individual-coach", CompanyId: companyId), cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            response = await httpClient.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenAI Individual Coach returned HTTP {(int)response.StatusCode}.");
            var outputText = ExtractOutputText(responseBody);
            var result = JsonSerializer.Deserialize<IndividualCoachResult>(outputText, JsonOptions) ?? throw new InvalidOperationException("Individual Coach response was empty.");
            await SaveLogAsync(settings, model, endpoint, companyId, startedAt, (int)response.StatusCode, true, requestJson, responseBody, outputText, null, cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await SaveLogAsync(settings, model, endpoint, companyId, startedAt, response is null ? null : (int)response.StatusCode, false, requestJson, responseBody, null, exception, cancellationToken);
            throw;
        }
    }

    private async Task SaveLogAsync(AiAgentRuntimeSettings settings, string model, string endpoint, string companyId, DateTime startedAt, int? status, bool success, string request, string? response, string? result, Exception? exception, CancellationToken cancellationToken)
    {
        await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
            settings, model, "responses.skopos-individual-coach", endpoint,
            new("skopos-individual-coach", CompanyId: companyId, ContextEntityKeys: settings.ContextEntityKeys),
            startedAt, status, success, request, response, result, exception, model), cancellationToken);
    }

    private static string ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        foreach (var output in document.RootElement.GetProperty("output").EnumerateArray())
            foreach (var content in output.GetProperty("content").EnumerateArray())
                if (content.TryGetProperty("type", out var type) && type.GetString() == "output_text" && content.TryGetProperty("text", out var text)) return text.GetString() ?? "{}";
        throw new InvalidOperationException("Individual Coach did not return output_text.");
    }

    private static readonly object CompetencyKey = new { type = "string", @enum = new[] { "service", "cadence", "qualification", "objections", "proposal", "product", "execution" } };
    private static readonly object ShortText = new { type = "string", maxLength = 140 };
    private static readonly object LongText = new { type = "string", maxLength = 500 };
    private static readonly object Score = new { type = "integer", minimum = 0, maximum = 100 };

    private static readonly object Schema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "summary", "objective", "strengths", "items" },
        properties = new
        {
            summary = new { type = "string", maxLength = 700 },
            objective = new { type = "string", maxLength = 400 },
            strengths = new
            {
                type = "array", maxItems = 3,
                items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "key", "title", "summary", "score" },
                    properties = new { key = CompetencyKey, title = ShortText, summary = LongText, score = Score }
                }
            },
            items = new
            {
                type = "array", minItems = 1, maxItems = 3,
                items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "competencyKey", "title", "baselineScore", "targetScore", "action", "measurement", "resource", "dueInDays", "evidenceIds", "guide" },
                    properties = new
                    {
                        competencyKey = CompetencyKey,
                        title = ShortText,
                        baselineScore = new { type = new[] { "integer", "null" }, minimum = 0, maximum = 100 },
                        targetScore = new { type = new[] { "integer", "null" }, minimum = 0, maximum = 100 },
                        action = LongText,
                        measurement = LongText,
                        resource = LongText,
                        dueInDays = new { type = "integer", minimum = 7, maximum = 90 },
                        evidenceIds = new { type = "array", maxItems = 5, items = new { type = "string" } },
                        guide = new
                        {
                            type = "object", additionalProperties = false,
                            required = new[] { "diagnosis", "whyItMatters", "firstAction", "managerSupport", "successCriteria", "steps" },
                            properties = new
                            {
                                diagnosis = LongText, whyItMatters = LongText, firstAction = LongText,
                                managerSupport = LongText, successCriteria = LongText,
                                steps = new
                                {
                                    type = "array", minItems = 2, maxItems = 4,
                                    items = new
                                    {
                                        type = "object", additionalProperties = false,
                                        required = new[] { "title", "instruction", "deliverable", "dueInDays" },
                                        properties = new { title = ShortText, instruction = LongText, deliverable = LongText, dueInDays = new { type = "integer", minimum = 1, maximum = 90 } }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    };
}

public sealed record IndividualCoachResult(string Summary, string Objective, IReadOnlyCollection<IndividualCoachStrength> Strengths, IReadOnlyCollection<IndividualCoachItem> Items);
public sealed record IndividualCoachStrength(string Key, string Title, string Summary, int Score);
public sealed record IndividualCoachItem(string CompetencyKey, string Title, int? BaselineScore, int? TargetScore, string Action, string Measurement, string Resource, int DueInDays, IReadOnlyCollection<string> EvidenceIds, IndividualDevelopmentGuide? Guide = null);
public sealed record IndividualDevelopmentGuide(string Diagnosis, string WhyItMatters, string FirstAction, string ManagerSupport, string SuccessCriteria, IReadOnlyCollection<IndividualDevelopmentStep> Steps);
public sealed record IndividualDevelopmentStep(string Title, string Instruction, string Deliverable, int DueInDays);

internal sealed class SkoposContextBudgetException() : InvalidOperationException(
    "O contexto do PDI excedeu o orçamento de 32 mil caracteres. Nenhuma chamada de IA foi feita; o roteiro determinístico precisa de revisão pelo gestor.");
