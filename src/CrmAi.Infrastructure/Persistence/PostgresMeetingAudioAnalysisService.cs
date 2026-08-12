using System.Text;
using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresMeetingAudioAnalysisService(
    NpgsqlDataSource dataSource,
    IAiAgentRuntimeSettingsRepository agentSettingsRepository,
    IOpenAiMeetingAudioClient openAiClient) : IMeetingAudioAnalysisService
{
    private const string MeetingAgentKey = "meeting-service-analysis";
    private const string CallAgentKey = "call-audio-analysis";

    public async Task<bool> ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        var recordingId = GetString(opportunityEvent.Data, "recordingId");
        if (string.IsNullOrWhiteSpace(recordingId) || !Guid.TryParse(recordingId, out var parsedRecordingId))
        {
            return false;
        }

        var recording = await LoadRecordingAsync(parsedRecordingId, cancellationToken);
        if (recording is null)
        {
            return false;
        }

        try
        {
            await UpdateStatusAsync(parsedRecordingId, "transcribing", null, cancellationToken);
            var settings = await agentSettingsRepository.GetAsync(ResolveAgentKey(recording.SourceKind), recording.CompanyId, cancellationToken);
            if (!settings.IsActive)
            {
                await UpdateStatusAsync(parsedRecordingId, "skipped", "Agent de analise do atendimento inativo.", cancellationToken);
                return false;
            }

            var invocationContext = BuildInvocationContext(recording, settings);
            var transcript = await openAiClient.TranscribeAsync(settings, recording.FileName, recording.MimeType, recording.Content, invocationContext, cancellationToken);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                await UpdateStatusAsync(parsedRecordingId, "failed", "Transcricao vazia retornada pela IA.", cancellationToken);
                return false;
            }

            await UpdateTranscriptAsync(parsedRecordingId, transcript, "analyzing", cancellationToken);
            var selectedContext = await LoadSelectedContextAsync(recording, settings.ContextEntityKeys, cancellationToken);
            var analysis = await openAiClient.AnalyzeAsync(settings, new MeetingAudioAnalysisInput(
                transcript,
                settings.ContextEntityKeys.Contains("opportunity") ? recording.OpportunityName : null,
                settings.ContextEntityKeys.Contains("account") ? recording.AccountName : null,
                settings.ContextEntityKeys.Contains("activities") ? recording.ActivityTitle : null,
                settings.ContextEntityKeys.Contains("activities") ? recording.ActivityNotes : null,
                selectedContext.Notes,
                selectedContext.Contacts,
                selectedContext.Activities,
                selectedContext.AgentInsights), invocationContext, cancellationToken);

            await SaveAnalysisAsync(parsedRecordingId, transcript, FormatSummary(analysis), cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            await UpdateStatusAsync(parsedRecordingId, "failed", exception.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task<MeetingSelectedContext> LoadSelectedContextAsync(MeetingAudioRecordingPayload recording, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(recording.OpportunityId, out var opportunityId)) return new([], [], [], []);
        var enabled = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        async Task<string[]> ReadAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("opportunityId", opportunityId);
            var values = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
            return values.ToArray();
        }

        var notes = enabled.Contains("notes")
            ? await ReadAsync("select text from notes where opportunity_id = @opportunityId order by created_at desc limit 20") : [];
        var contacts = enabled.Contains("contacts")
            ? await ReadAsync("select concat_ws(' | ', name, role, email, phone) from vw_ai_agent_contact_context where opportunity_id = @opportunityId order by name limit 30") : [];
        var activities = enabled.Contains("activities")
            ? await ReadAsync("select concat_ws(' | ', title, activity_type, channel, status, date_at::text, notes) from activities where opportunity_id = @opportunityId order by date_at desc limit 30") : [];
        var insights = enabled.Contains("agent_insights")
            ? await ReadAsync("select concat_ws(' | ', title, message, kind, status) from vw_ai_agent_insight_context where opportunity_id = @opportunityId order by updated_at desc limit 20") : [];
        return new(notes, contacts, activities, insights);
    }

    private sealed record MeetingSelectedContext(string[] Notes, string[] Contacts, string[] Activities, string[] AgentInsights);

    private async Task<MeetingAudioRecordingPayload?> LoadRecordingAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        const string sql = """
            select
                mar.id,
                mar.meeting_id,
                mar.source_kind,
                mar.activity_id,
                mar.opportunity_id,
                mar.account_id,
                mar.file_name,
                mar.mime_type,
                mar.audio_content,
                mar.company_id,
                o.name as opportunity_name,
                a.name as account_name,
                act.title as activity_title,
                act.notes as activity_notes
            from meeting_audio_recordings mar
            left join opportunities o on o.id = mar.opportunity_id
            left join accounts a on a.id = mar.account_id
            left join activities act on act.id = mar.activity_id
            where mar.id = @recordingId
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("recordingId", recordingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MeetingAudioRecordingPayload(
            reader.GetGuid(0).ToString(),
            reader.GetString(1),
            ReadNullableGuid(reader, 3),
            ReadNullableGuid(reader, 4),
            ReadNullableGuid(reader, 5),
            reader.GetString(6),
            reader.GetString(7),
            (byte[])reader[8],
            ReadNullableString(reader, 10),
            ReadNullableString(reader, 11),
            ReadNullableString(reader, 12),
            ReadNullableString(reader, 13),
            ReadNullableGuid(reader, 9),
            reader.GetString(2));
    }

    private async Task UpdateStatusAsync(Guid recordingId, string status, string? error, CancellationToken cancellationToken)
    {
        const string sql = """
            update meeting_audio_recordings
            set status = @status,
                processing_error = @error,
                updated_at = now()
            where id = @recordingId
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("recordingId", recordingId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(error) ? DBNull.Value : error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateTranscriptAsync(Guid recordingId, string transcript, string status, CancellationToken cancellationToken)
    {
        const string sql = """
            update meeting_audio_recordings
            set transcript = @transcript,
                status = @status,
                processing_error = null,
                updated_at = now()
            where id = @recordingId
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("recordingId", recordingId);
        command.Parameters.AddWithValue("transcript", transcript);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveAnalysisAsync(Guid recordingId, string transcript, string summary, CancellationToken cancellationToken)
    {
        const string sql = """
            update meeting_audio_recordings
            set transcript = @transcript,
                summary = @summary,
                status = 'ready',
                processing_error = null,
                transcribed_at = now(),
                updated_at = now()
            where id = @recordingId
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("recordingId", recordingId);
        command.Parameters.AddWithValue("transcript", transcript);
        command.Parameters.AddWithValue("summary", summary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatSummary(OpenAiMeetingAudioAnalysisResponse analysis)
    {
        var builder = new StringBuilder();
        builder.AppendLine(analysis.Summary.Trim());
        AppendList(builder, "Objeções identificadas", analysis.Objections);
        AppendList(builder, "Oportunidades para quebrar objeções", analysis.ObjectionBreakOpportunities);
        builder.AppendLine();
        builder.Append("Proximo passo: ").Append(analysis.NextStep.Trim());
        return builder.ToString().Trim();
    }

    public static string ResolveAgentKey(string? sourceKind) =>
        string.Equals(sourceKind, "whatsapp_call", StringComparison.OrdinalIgnoreCase)
            ? CallAgentKey
            : MeetingAgentKey;

    private static AiAgentInvocationContext BuildInvocationContext(MeetingAudioRecordingPayload recording, AiAgentRuntimeSettings settings) =>
        new(
            PlatformArea: string.Equals(recording.SourceKind, "whatsapp_call", StringComparison.OrdinalIgnoreCase) ? "call-audio" : "meeting-audio",
            CompanyId: recording.CompanyId,
            OpportunityId: recording.OpportunityId,
            MeetingAudioRecordingId: recording.Id,
            ActivityId: recording.ActivityId,
            AccountId: recording.AccountId,
            ContextEntityKeys: settings.ContextEntityKeys,
            Metadata: new Dictionary<string, object?>
            {
                ["meetingId"] = recording.MeetingId,
                ["sourceKind"] = recording.SourceKind,
                ["fileName"] = recording.FileName,
                ["mimeType"] = recording.MimeType,
                ["opportunityName"] = recording.OpportunityName,
                ["accountName"] = recording.AccountName,
                ["activityTitle"] = recording.ActivityTitle
            });

    private static void AppendList(StringBuilder builder, string title, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine().AppendLine().AppendLine(title);
        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            builder.Append("- ").AppendLine(item.Trim());
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string? ReadNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? ReadNullableGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal).ToString();
}
