using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmAi.Application;

namespace CrmAi.Infrastructure.Persistence;

internal static class SuggestionVerificationCache
{
    public static string Fingerprint(
        SuggestionCompletionVerificationInput input,
        AiAgentRuntimeSettings settings,
        string effectiveModel,
        DateTime evaluatedAtUtc,
        string evidenceSelectionMode = "full")
    {
        var json = JsonSerializer.Serialize(new
        {
            version = "verification-cache-v1:lossless-context-v1",
            contextPolicy = OpenAiSuggestionCompletionVerificationClient.ContextPolicyFingerprint,
            evidenceSelectionMode,
            input.SuggestionId,
            input.SuggestionType,
            input.Title,
            input.Description,
            input.CreatedAt,
            input.DueAt,
            input.Payload,
            evidence = input.Evidence.OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new { item.Id, item.Type, item.OccurredAt, item.BeforeSuggestion, item.Summary, item.SourceStreamId }),
            settings.IsActive,
            settings.Provider,
            model = effectiveModel,
            settings.Instructions,
            settings.TimeZoneId,
            contextEntityKeys = settings.ContextEntityKeys.OrderBy(key => key, StringComparer.Ordinal),
            evaluationDateUtc = evaluatedAtUtc.Date,
            dueDatePassed = input.DueAt is { } dueAt && dueAt <= evaluatedAtUtc
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static bool CanReuse(string previousStatus, string? previousFingerprint, string fingerprint) =>
        previousStatus is "unfulfilled" or "inconclusive"
        && string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal);
}
