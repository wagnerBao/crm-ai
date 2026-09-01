using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmAi.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class SuggestionQualityAuditHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<SuggestionQualityAuditHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processed = await scope.ServiceProvider.GetRequiredService<SuggestionQualityAuditProcessor>()
                    .ProcessNextAsync(stoppingToken);
                if (!processed) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Suggestion quality audit worker failed before completing the claimed report.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}

public sealed class SuggestionQualityAuditProcessor(
    NpgsqlDataSource dataSource,
    IAiAgentRuntimeSettingsRepository settingsRepository,
    IOpenAiSuggestionQualityAuditClient client)
{
    private const string FeedbackScoringGuidance = "Treat accepted feedback backed by a created activity or opportunity as the strongest positive signal (1.0). Treat already_completed as positive supporting evidence with lower strength (0.6), because the suggested action was relevant but was not created from the suggestion. Neutral and negative feedback are not positive signals.";
    private sealed record ClaimedReport(Guid Id, Guid? CompanyId, string? AgentKey, int AttemptCount, bool LowSample, string FiltersJson, string MetricsJson);
    private sealed record FeedbackRow(Guid Id, string Sentiment, string Action, string Timeliness, string? Reason, string SnapshotJson, DateTime CreatedAt);

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var report = await ClaimAsync(cancellationToken);
        if (report is null) return false;

        try
        {
            var companyId = report.CompanyId?.ToString();
            var auditSettings = await settingsRepository.GetAsync("suggestion-quality-audit", companyId, cancellationToken);
            if (!auditSettings.IsActive) throw new InvalidOperationException("O agente suggestion-quality-audit esta inativo.");
            var evaluatedAgentKey = string.IsNullOrWhiteSpace(report.AgentKey) ? "whatsapp-conversation-analysis" : report.AgentKey;
            var evaluatedSettings = await settingsRepository.GetAsync(evaluatedAgentKey, companyId, cancellationToken);
            var feedbacks = await LoadFeedbacksAsync(report, cancellationToken);
            if (feedbacks.Count == 0) throw new InvalidOperationException("Nenhum feedback permaneceu disponivel para o relatorio.");

            using var filtersDocument = JsonDocument.Parse(report.FiltersJson);
            using var metricsDocument = JsonDocument.Parse(report.MetricsJson);
            var input = new SuggestionQualityAuditInput(
                metricsDocument.RootElement.Clone(),
                filtersDocument.RootElement.Clone(),
                report.LowSample,
                FeedbackScoringGuidance,
                evaluatedAgentKey,
                evaluatedSettings.Model,
                evaluatedSettings.SystemPrompt,
                evaluatedSettings.ContextInstructions,
                feedbacks);
            var result = await client.AnalyzeAsync(
                auditSettings,
                input,
                new AiAgentInvocationContext(
                    "admin.suggestion-quality",
                    companyId,
                    ContextEntityKeys: auditSettings.ContextEntityKeys,
                    Metadata: new Dictionary<string, object?> { ["reportId"] = report.Id, ["evaluatedAgentKey"] = evaluatedAgentKey }),
                cancellationToken);
            await CompleteAsync(report.Id, auditSettings.Model, Fingerprint(auditSettings.Instructions), result, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailOrRetryAsync(report, exception, cancellationToken);
        }
        return true;
    }

    private async Task<ClaimedReport?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            with candidate as (
                select id
                from ai_suggestion_quality_reports
                where attempt_count < 3
                  and (
                    (status = 'pending' and next_attempt_at <= now())
                    or (status = 'processing' and updated_at < now() - interval '15 minutes')
                  )
                order by created_at
                for update skip locked
                limit 1
            )
            update ai_suggestion_quality_reports report
            set status = 'processing', attempt_count = report.attempt_count + 1,
                started_at = coalesce(report.started_at, now()), updated_at = now(), error_message = null
            from candidate
            where report.id = candidate.id
            returning report.id, report.company_id, report.agent_key, report.attempt_count,
                      report.low_sample, report.filters_json::text, report.metrics_json::text;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        ClaimedReport? report = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            report = new(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader.GetString(6));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return report;
    }

    private async Task<IReadOnlyCollection<SuggestionQualityFeedbackEvidence>> LoadFeedbacksAsync(ClaimedReport report, CancellationToken cancellationToken)
    {
        using var filters = JsonDocument.Parse(report.FiltersJson);
        var root = filters.RootElement;
        var from = ReadDate(root, "from") ?? DateTime.UtcNow.AddDays(-30);
        var to = ReadDate(root, "to") ?? DateTime.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            select id, sentiment, action, timeliness, reason, suggestion_snapshot::text, created_at
            from ai_agent_suggestion_feedback
            where created_at >= @from and created_at < @to
              and (@companyId::uuid is null or company_id = @companyId::uuid)
              and (@agentKey::text is null or agent_key = @agentKey::text)
            order by created_at desc
            limit 600;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.Add("companyId", NpgsqlDbType.Uuid).Value = report.CompanyId is null ? DBNull.Value : report.CompanyId.Value;
        command.Parameters.Add("agentKey", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(report.AgentKey) ? DBNull.Value : report.AgentKey;
        var rows = new List<FeedbackRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetDateTime(6)));
        }

        var selected = rows.GroupBy(item => item.Sentiment, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Take(50))
            .ToList();
        selected.AddRange(rows.Where(item => selected.All(existing => existing.Id != item.Id)).Take(150 - selected.Count));
        return selected.Take(150).Select(item =>
        {
            using var snapshot = JsonDocument.Parse(item.SnapshotJson);
            return new SuggestionQualityFeedbackEvidence(
                item.Id.ToString(),
                item.Sentiment,
                item.Action,
                SignalStrength(item.Action),
                item.Timeliness,
                item.Reason,
                snapshot.RootElement.Clone());
        }).ToArray();
    }

    private static double SignalStrength(string action) => action switch
    {
        "accepted" => 1.0,
        "already_completed" => 0.6,
        "disliked" => -1.0,
        _ => 0.0
    };

    private async Task CompleteAsync(Guid id, string model, string fingerprint, SuggestionQualityAuditResult result, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update ai_suggestion_quality_reports set status = 'completed', result_json = @result, model = @model, prompt_fingerprint = @fingerprint, completed_at = now(), updated_at = now(), error_message = null where id = @id;",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("model", model);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.Add("result", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task FailOrRetryAsync(ClaimedReport report, Exception exception, CancellationToken cancellationToken)
    {
        var terminal = report.AttemptCount >= 3;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update ai_suggestion_quality_reports set status = @status, error_message = @error, next_attempt_at = now() + (@delayMinutes * interval '1 minute'), updated_at = now() where id = @id;",
            connection);
        command.Parameters.AddWithValue("id", report.Id);
        command.Parameters.AddWithValue("status", terminal ? "failed" : "pending");
        command.Parameters.AddWithValue("error", Truncate(exception.Message, 1000));
        command.Parameters.AddWithValue("delayMinutes", terminal ? 0 : (int)Math.Pow(2, report.AttemptCount - 1));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTime? ReadDate(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
