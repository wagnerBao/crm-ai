using CrmAi.Domain;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrmAi.Application;

public sealed class WhatsappConversationAnalysisAgent(
    IOpenAiWhatsappConversationAnalysisClient openAiClient,
    IAiAgentRuntimeSettingsRepository agentSettingsRepository,
    IWhatsappSuggestionContextRepository suggestionContextRepository,
    IWhatsappScorecardContextRepository? scorecardContextRepository = null) : IWhatsappConversationAnalysisAgent
{
    private const string AgentKey = "whatsapp-conversation-analysis";

    public async Task<WhatsappConversationAnalysisResult?> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken)
    {
        var settings = await agentSettingsRepository.GetAsync(AgentKey, context.Opportunity.CompanyId, cancellationToken);
        var input = WhatsappConversationAnalysisInput.FromContext(context, settings.ContextEntityKeys);
        if (string.IsNullOrWhiteSpace(input.NewTranscript))
        {
            return null;
        }

        if (!settings.IsActive)
        {
            return null;
        }

        var semanticContext = await suggestionContextRepository.GetAsync(
            context.Opportunity.CompanyId,
            input.Conversation.ContactId,
            cancellationToken);
        input = input with
        {
            ExistingSuggestions = semanticContext.ExistingSuggestions,
            ExistingOpenOpportunities = semanticContext.ExistingOpenOpportunities
        };
        var scorecardContext = scorecardContextRepository is null
            ? null
            : await scorecardContextRepository.GetAsync(context.TriggerEvent, cancellationToken);
        input = input with { ScorecardTemplate = scorecardContext?.ToInput() };

        var invocationContext = new AiAgentInvocationContext(
            PlatformArea: "whatsapp",
            CompanyId: context.Opportunity.CompanyId,
            OpportunityId: context.Opportunity.Id,
            WhatsappConversationId: input.Conversation.ConversationId,
            ContactId: input.Conversation.ContactId,
            UserId: context.TriggerEvent.UserId ?? context.Opportunity.OwnerUserId,
            ContextEntityKeys: settings.ContextEntityKeys,
            Metadata: new Dictionary<string, object?>
            {
                ["triggerEventId"] = context.TriggerEvent.EventId,
                ["triggerEventType"] = context.TriggerEvent.Type,
                ["messageCount"] = input.Conversation.MessageCount,
                ["firstMessageAt"] = input.Conversation.FirstMessageAt,
                ["latestMessageAt"] = input.Conversation.LatestMessageAt,
                ["processedUntil"] = input.Conversation.ProcessedUntil
            });

        return await AnalyzeCoreAsync(settings, input, scorecardContext, invocationContext, cancellationToken);
    }

    public async Task<WhatsappConversationAnalysisResult?> AnalyzeContactAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        var companyId = GetString(opportunityEvent, "companyId");
        var settings = await agentSettingsRepository.GetAsync(AgentKey, companyId, cancellationToken);
        var input = WhatsappConversationAnalysisInput.FromContactEvent(opportunityEvent);
        if (string.IsNullOrWhiteSpace(input.NewTranscript) || !settings.IsActive)
        {
            return null;
        }

        var semanticContext = await suggestionContextRepository.GetAsync(
            companyId,
            input.Conversation.ContactId,
            cancellationToken);
        input = input with
        {
            ExistingSuggestions = semanticContext.ExistingSuggestions,
            ExistingOpenOpportunities = semanticContext.ExistingOpenOpportunities
        };
        var scorecardContext = scorecardContextRepository is null
            ? null
            : await scorecardContextRepository.GetAsync(opportunityEvent, cancellationToken);
        input = input with { ScorecardTemplate = scorecardContext?.ToInput() };

        var invocationContext = new AiAgentInvocationContext(
            PlatformArea: "whatsapp",
            CompanyId: companyId,
            OpportunityId: null,
            WhatsappConversationId: input.Conversation.ConversationId,
            ContactId: input.Conversation.ContactId,
            UserId: opportunityEvent.UserId ?? GetString(opportunityEvent, "ownerUserId"),
            ContextEntityKeys: settings.ContextEntityKeys,
            Metadata: new Dictionary<string, object?>
            {
                ["triggerEventId"] = opportunityEvent.EventId,
                ["triggerEventType"] = opportunityEvent.Type,
                ["scope"] = "contact",
                ["messageCount"] = input.Conversation.MessageCount,
                ["latestMessageAt"] = input.Conversation.LatestMessageAt
            });

        return await AnalyzeCoreAsync(settings, input, scorecardContext, invocationContext, cancellationToken);
    }

    private async Task<WhatsappConversationAnalysisResult> AnalyzeCoreAsync(
        AiAgentRuntimeSettings settings,
        WhatsappConversationAnalysisInput input,
        WhatsappScorecardContext? scorecardContext,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var response = await openAiClient.AnalyzeAsync(settings, input, invocationContext, cancellationToken);
        var dueAt = ParseDateTime(response.ActivityDueAt);
        var nextSteps = CleanOptional(response.NextSteps);
        var activityTitle = CleanNullableText(response.ActivityTitle);
        var activityNotes = CleanNullableText(response.ActivityNotes);
        var shouldCreateActivity = response.ShouldCreateActivity && !string.IsNullOrWhiteSpace(activityTitle);

        // The model exposes nextSteps and the activity suggestion as separate fields. Keep the CRM
        // actionable even when it returns a concrete next step but leaves the activity flags unset.
        if (nextSteps.Count > 0)
        {
            shouldCreateActivity = true;
            activityTitle ??= BuildFallbackActivityTitle(nextSteps.First());
            activityNotes ??= string.Join("\n", nextSteps.Select(step => $"- {step}"));
        }

        var activityIntentKey = CleanIntentKey(response.ActivityIntentKey);
        if (shouldCreateActivity && string.IsNullOrWhiteSpace(activityIntentKey))
        {
            activityIntentKey = BuildFallbackActivityIntentKey(activityTitle!);
        }
        var scorecard = NormalizeScorecard(scorecardContext, response.ScorecardItems ?? [], input.NewTranscript);
        var fingerprintInput = string.Concat(
            settings.Instructions,
            "\nwhatsapp-scorecard-schema:1.0\n",
            JsonSerializer.Serialize(input.ScorecardTemplate));

        return new WhatsappConversationAnalysisResult(
            ConversationSummary: CleanText(response.ConversationSummary, input.PreviousSummary ?? input.NewTranscript),
            CommercialObservations: CleanNullableText(response.CommercialObservations),
            NextSteps: nextSteps,
            Insights: CleanOptional(response.Insights),
            ShouldCreateNote: response.ShouldCreateNote,
            NoteText: CleanNullableText(response.NoteText),
            ShouldCreateActivity: shouldCreateActivity,
            ActivityTitle: activityTitle,
            ActivityNotes: activityNotes,
            ActivityDueAt: dueAt,
            ShouldCreateOpportunity: response.ShouldCreateOpportunity,
            OpportunityTitle: CleanNullableText(response.OpportunityTitle),
            OpportunityDescription: CleanNullableText(response.OpportunityDescription),
            ActivityMatchingSuggestionId: ValidateSuggestionMatch(
                response.ActivityMatchingSuggestionId, "activity", input.ExistingSuggestions),
            ActivityIntentKey: activityIntentKey,
            OpportunityMatchingSuggestionId: ValidateSuggestionMatch(
                response.OpportunityMatchingSuggestionId, "opportunity", input.ExistingSuggestions),
            OpportunityIntentKey: CleanIntentKey(response.OpportunityIntentKey),
            MatchingOpenOpportunityId: ValidateOpenOpportunityMatch(
                response.MatchingOpenOpportunityId, input.ExistingOpenOpportunities),
            ConfidenceScore: Math.Clamp(response.ConfidenceScore, 0, 100),
            Reasons: Clean(response.Reasons),
            GenerationModel: settings.Model,
            PromptFingerprint: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant(),
            Scorecard: scorecard);
    }

    private static WhatsappConversationScorecardResult? NormalizeScorecard(
        WhatsappScorecardContext? context,
        IReadOnlyCollection<OpenAiConversationScorecardItem> modelItems,
        string newTranscript)
    {
        if (context is null) return null;
        var byKey = modelItems
            .Where(item => !string.IsNullOrWhiteSpace(item.CriterionKey))
            .GroupBy(item => item.CriterionKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var previousEvidence = context.PreviousDailyItems
            .SelectMany(item => item.Evidence)
            .Select(item => item.Excerpt);
        var evidenceCorpus = NormalizeWhitespace(string.Join("\n", previousEvidence.Prepend(newTranscript)));

        var items = context.Criteria.Select(criterion =>
        {
            if (!byKey.TryGetValue(criterion.Key, out var modelItem))
            {
                return new WhatsappConversationScorecardItemResult(
                    criterion.Id, criterion.Key, criterion.Title, criterion.Weight, criterion.ScoreMin, 0,
                    "Sem cobertura suficiente para avaliar este critério.", null, []);
            }

            var evidence = (modelItem.Evidence ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Excerpt))
                .Where(item => evidenceCorpus.Contains(NormalizeWhitespace(item.Excerpt), StringComparison.OrdinalIgnoreCase))
                .Select(item => new WhatsappConversationScorecardEvidenceResult(
                    Truncate(item.Excerpt.Trim(), 500),
                    string.IsNullOrWhiteSpace(item.Participant) ? null : Truncate(item.Participant.Trim(), 120),
                    "transcript",
                    Math.Clamp(item.ConfidenceScore, 0, 100)))
                .DistinctBy(item => item.Excerpt, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();
            var covered = evidence.Length > 0;
            return new WhatsappConversationScorecardItemResult(
                criterion.Id,
                criterion.Key,
                criterion.Title,
                criterion.Weight,
                Math.Clamp(modelItem.Score, criterion.ScoreMin, criterion.ScoreMax),
                covered ? Math.Clamp(modelItem.ConfidenceScore, 0, 100) : 0,
                covered
                    ? Truncate(string.IsNullOrWhiteSpace(modelItem.Justification) ? "Avaliação sustentada pelas evidências registradas." : modelItem.Justification.Trim(), 1500)
                    : "Sem cobertura suficiente: nenhuma evidência literal válida foi localizada na conversa do dia.",
                string.IsNullOrWhiteSpace(modelItem.Recommendation) ? null : Truncate(modelItem.Recommendation.Trim(), 1000),
                evidence);
        }).ToArray();

        return new WhatsappConversationScorecardResult(
            context.TemplateId,
            context.TemplateKey,
            context.TemplateVersion,
            context.TemplateName,
            items);
    }

    private static string? GetString(OpportunityEvent opportunityEvent, string key) =>
        opportunityEvent.Data.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static string CleanText(string? value, string fallback)
    {
        var normalized = CleanNullableText(value);
        return string.IsNullOrWhiteSpace(normalized) ? fallback.Trim() : normalized;
    }

    private static string? CleanNullableText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? ValidateSuggestionMatch(
        string? value,
        string suggestionType,
        IReadOnlyCollection<WhatsappSuggestionCandidate> candidates)
    {
        if (!Guid.TryParse(value, out var parsed))
        {
            return null;
        }

        var normalized = parsed.ToString();
        return candidates.Any(candidate =>
            candidate.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            && candidate.SuggestionType.Equals(suggestionType, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : null;
    }

    private static string? ValidateOpenOpportunityMatch(
        string? value,
        IReadOnlyCollection<WhatsappOpenOpportunityCandidate> candidates)
    {
        if (!Guid.TryParse(value, out var parsed))
        {
            return null;
        }

        var normalized = parsed.ToString();
        return candidates.Any(candidate => candidate.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : null;
    }

    private static string? CleanIntentKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join('_', value.Trim().ToLowerInvariant()
            .Split([' ', '-', '/', '.'], StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }

    private static string BuildFallbackActivityTitle(string nextStep)
    {
        var sentences = nextStep
            .Split(['.', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(sentence => sentence.TrimStart('-', '•', ' '))
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToArray();
        var title = sentences.FirstOrDefault(sentence =>
                !sentence.StartsWith("Responsável sugerido:", StringComparison.OrdinalIgnoreCase)
                && !sentence.StartsWith("Responsavel sugerido:", StringComparison.OrdinalIgnoreCase))
            ?? sentences.FirstOrDefault()
            ?? "Executar próximo passo da conversa";

        return Truncate(title, 160);
    }

    private static string BuildFallbackActivityIntentKey(string title)
    {
        var normalized = new string(title
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_')
            .ToArray());
        var compact = string.Join('_', normalized.Split('_', StringSplitOptions.RemoveEmptyEntries));
        return Truncate($"next_step_{compact}", 160);
    }

    private static IReadOnlyCollection<string> Clean(IReadOnlyCollection<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("Conversa analisada e consolidada.")
            .ToArray();

    private static IReadOnlyCollection<string> CleanOptional(IReadOnlyCollection<string>? values)
        => (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
