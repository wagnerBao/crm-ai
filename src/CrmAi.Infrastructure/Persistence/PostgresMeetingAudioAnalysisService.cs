using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const string AnalysisSchemaVersion = "1.0";
    private const int StaleExecutionMinutes = 120;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        Guid? analysisResultId = null;
        try
        {
            var agentKey = ResolveAgentKey(recording.SourceKind);
            var requestedAnalysisResultId = GetGuid(opportunityEvent.Data, "analysisResultId");
            var settings = await agentSettingsRepository.GetAsync(agentKey, recording.CompanyId, cancellationToken);
            if (!settings.IsActive)
            {
                await FailRequestedExecutionAsync(requestedAnalysisResultId, "Agent de analise do atendimento inativo.", cancellationToken);
                await UpdateStatusAsync(parsedRecordingId, "skipped", "Agent de analise do atendimento inativo.", cancellationToken);
                return false;
            }

            var promptFingerprint = PromptFingerprint(settings);
            analysisResultId = await BeginAnalysisAsync(
                recording,
                parsedRecordingId,
                agentKey,
                settings.Model,
                promptFingerprint,
                requestedAnalysisResultId,
                GetBoolean(opportunityEvent.Data, "forceReprocess"),
                cancellationToken);
            if (!analysisResultId.HasValue)
            {
                return false;
            }

            var invocationContext = BuildInvocationContext(recording, settings);
            var transcript = recording.Transcript;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                await UpdateStatusAsync(parsedRecordingId, "transcribing", null, cancellationToken);
                transcript = await openAiClient.TranscribeAsync(settings, recording.FileName, recording.MimeType, recording.Content, invocationContext, cancellationToken);
            }
            else
            {
                await UpdateStatusAsync(parsedRecordingId, "analyzing", null, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                await MarkAnalysisFailedAsync(analysisResultId.Value, "Transcricao vazia retornada pela IA.", cancellationToken);
                await UpdateStatusAsync(parsedRecordingId, "failed", "Transcricao vazia retornada pela IA.", cancellationToken);
                return false;
            }

            if (string.IsNullOrWhiteSpace(recording.Transcript))
            {
                await UpdateTranscriptAsync(parsedRecordingId, transcript, "analyzing", cancellationToken);
            }
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

            await SaveAnalysisAsync(
                recording,
                parsedRecordingId,
                transcript,
                FormatSummary(analysis),
                analysis,
                settings,
                analysisResultId.Value,
                agentKey,
                promptFingerprint,
                cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            if (analysisResultId.HasValue)
            {
                await MarkAnalysisFailedBestEffortAsync(analysisResultId.Value, exception.Message);
            }
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
                act.notes as activity_notes,
                contact.id as contact_id,
                act.owner_user_id,
                mar.transcript
            from meeting_audio_recordings mar
            left join opportunities o on o.id = mar.opportunity_id and o.company_id = mar.company_id
            left join accounts a on a.id = mar.account_id and a.company_id = mar.company_id
            left join activities act on act.id = mar.activity_id and act.company_id = mar.company_id
            left join contacts contact on contact.id = act.contact_id and contact.company_id = mar.company_id
            where mar.id = @recordingId and mar.company_id is not null
              and (mar.opportunity_id is null or o.id is not null)
              and (mar.account_id is null or a.id is not null)
              and (mar.activity_id is null or act.id is not null)
              and (act.contact_id is null or contact.id is not null)
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
            reader.GetString(2),
            ReadNullableGuid(reader, 14),
            ReadNullableGuid(reader, 15),
            ReadNullableString(reader, 16));
    }

    private async Task<Guid?> BeginAnalysisAsync(
        MeetingAudioRecordingPayload recording,
        Guid recordingId,
        string agentKey,
        string model,
        string promptFingerprint,
        Guid? requestedAnalysisResultId,
        bool forceReprocess,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(recording.CompanyId, out var companyId))
        {
            throw new InvalidOperationException("A gravacao nao possui uma empresa valida para persistir a analise estruturada.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var staleCommand = new NpgsqlCommand($"""
            update conversation_analysis_results
            set analysis_status = 'failed', processing_error = 'Execucao expirada antes da conclusao.',
                updated_at = now(), is_current = false
            where company_id = @companyId and recording_id = @recordingId and agent_key = @agentKey
              and analysis_status in ('pending', 'processing')
              and updated_at < now() - interval '{StaleExecutionMinutes} minutes'
            """, connection))
        {
            staleCommand.Parameters.AddWithValue("companyId", companyId);
            staleCommand.Parameters.AddWithValue("recordingId", recordingId);
            staleCommand.Parameters.AddWithValue("agentKey", agentKey);
            await staleCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (requestedAnalysisResultId.HasValue)
        {
            await using var claimCommand = new NpgsqlCommand("""
                update conversation_analysis_results
                set analysis_status = 'processing', model = @model, prompt_fingerprint = @promptFingerprint,
                    schema_version = @schemaVersion, processing_error = null, updated_at = now()
                where id = @analysisResultId and company_id = @companyId and recording_id = @recordingId
                  and agent_key = @agentKey and analysis_status in ('pending', 'failed')
                returning id
                """, connection);
            claimCommand.Parameters.AddWithValue("analysisResultId", requestedAnalysisResultId.Value);
            claimCommand.Parameters.AddWithValue("companyId", companyId);
            claimCommand.Parameters.AddWithValue("recordingId", recordingId);
            claimCommand.Parameters.AddWithValue("agentKey", agentKey);
            claimCommand.Parameters.AddWithValue("model", model);
            claimCommand.Parameters.AddWithValue("promptFingerprint", promptFingerprint);
            claimCommand.Parameters.AddWithValue("schemaVersion", AnalysisSchemaVersion);
            try
            {
                var claimed = await claimCommand.ExecuteScalarAsync(cancellationToken);
                return claimed is Guid claimedId ? claimedId : null;
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(exception.ConstraintName, "ux_conversation_analysis_active", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        if (!forceReprocess)
        {
            await using var currentCommand = new NpgsqlCommand("""
                select exists (
                    select 1 from conversation_analysis_results
                    where company_id = @companyId and recording_id = @recordingId and agent_key = @agentKey
                      and schema_version = @schemaVersion and model = @model
                      and prompt_fingerprint = @promptFingerprint
                      and analysis_status = 'completed' and is_current
                )
                """, connection);
            currentCommand.Parameters.AddWithValue("companyId", companyId);
            currentCommand.Parameters.AddWithValue("recordingId", recordingId);
            currentCommand.Parameters.AddWithValue("agentKey", agentKey);
            currentCommand.Parameters.AddWithValue("schemaVersion", AnalysisSchemaVersion);
            currentCommand.Parameters.AddWithValue("model", model);
            currentCommand.Parameters.AddWithValue("promptFingerprint", promptFingerprint);
            if ((bool)(await currentCommand.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                return null;
            }
        }

        var analysisResultId = Guid.NewGuid();
        try
        {
            await using var insertCommand = new NpgsqlCommand("""
                insert into conversation_analysis_results
                    (id, company_id, recording_id, activity_id, opportunity_id, account_id, contact_id,
                     source_kind, agent_key, schema_version, analysis_status, analysis_json,
                     model, prompt_fingerprint, is_current, updated_at)
                values
                    (@id, @companyId, @recordingId, @activityId, @opportunityId, @accountId, @contactId,
                     @sourceKind, @agentKey, @schemaVersion, 'processing', '{}'::jsonb,
                     @model, @promptFingerprint, false, now())
                """, connection);
            insertCommand.Parameters.AddWithValue("id", analysisResultId);
            insertCommand.Parameters.AddWithValue("companyId", companyId);
            insertCommand.Parameters.AddWithValue("recordingId", recordingId);
            AddNullableGuid(insertCommand, "activityId", recording.ActivityId);
            AddNullableGuid(insertCommand, "opportunityId", recording.OpportunityId);
            AddNullableGuid(insertCommand, "accountId", recording.AccountId);
            AddNullableGuid(insertCommand, "contactId", recording.ContactId);
            insertCommand.Parameters.AddWithValue("sourceKind", recording.SourceKind);
            insertCommand.Parameters.AddWithValue("agentKey", agentKey);
            insertCommand.Parameters.AddWithValue("schemaVersion", AnalysisSchemaVersion);
            insertCommand.Parameters.AddWithValue("model", model);
            insertCommand.Parameters.AddWithValue("promptFingerprint", promptFingerprint);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            return analysisResultId;
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(exception.ConstraintName, "ux_conversation_analysis_active", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    private async Task FailRequestedExecutionAsync(Guid? analysisResultId, string error, CancellationToken cancellationToken)
    {
        if (!analysisResultId.HasValue)
        {
            return;
        }

        await MarkAnalysisFailedAsync(analysisResultId.Value, error, cancellationToken);
    }

    private async Task MarkAnalysisFailedAsync(Guid analysisResultId, string error, CancellationToken cancellationToken)
    {
        const string sql = """
            update conversation_analysis_results
            set analysis_status = 'failed', processing_error = @error, is_current = false, updated_at = now()
            where id = @analysisResultId and analysis_status in ('pending', 'processing')
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("analysisResultId", analysisResultId);
        command.Parameters.AddWithValue("error", Truncate(error, 4000));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkAnalysisFailedBestEffortAsync(Guid analysisResultId, string error)
    {
        try
        {
            await MarkAnalysisFailedAsync(analysisResultId, error, CancellationToken.None);
        }
        catch
        {
            // The original processing failure must remain the error reported to the queue.
        }
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

    private async Task SaveAnalysisAsync(
        MeetingAudioRecordingPayload recording,
        Guid recordingId,
        string transcript,
        string summary,
        OpenAiMeetingAudioAnalysisResponse analysis,
        AiAgentRuntimeSettings settings,
        Guid analysisResultId,
        string agentKey,
        string promptFingerprint,
        CancellationToken cancellationToken)
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("recordingId", recordingId);
        command.Parameters.AddWithValue("transcript", transcript);
        command.Parameters.AddWithValue("summary", summary);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var supersedeCommand = new NpgsqlCommand("""
            update conversation_analysis_results
            set is_current = false, updated_at = now()
            where company_id = @companyId and recording_id = @recordingId and agent_key = @agentKey
              and is_current and id <> @analysisResultId
            """, connection, transaction);
        supersedeCommand.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
        supersedeCommand.Parameters.AddWithValue("recordingId", recordingId);
        supersedeCommand.Parameters.AddWithValue("agentKey", agentKey);
        supersedeCommand.Parameters.AddWithValue("analysisResultId", analysisResultId);
        await supersedeCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var resultCommand = new NpgsqlCommand("""
            update conversation_analysis_results
            set analysis_status = 'completed', summary = @analysisSummary, next_step = @nextStep,
                confidence_score = @confidenceScore, analysis_json = @analysisJson,
                model = @model, prompt_fingerprint = @promptFingerprint,
                schema_version = @schemaVersion, is_current = true, processing_error = null,
                completed_at = now(), updated_at = now()
            where id = @analysisResultId and company_id = @companyId and recording_id = @recordingId
              and agent_key = @agentKey and analysis_status = 'processing'
            """, connection, transaction);
        resultCommand.Parameters.AddWithValue("analysisResultId", analysisResultId);
        resultCommand.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
        resultCommand.Parameters.AddWithValue("recordingId", recordingId);
        resultCommand.Parameters.AddWithValue("agentKey", agentKey);
        resultCommand.Parameters.AddWithValue("analysisSummary", analysis.Summary.Trim());
        resultCommand.Parameters.AddWithValue("nextStep", analysis.NextStep.Trim());
        resultCommand.Parameters.AddWithValue("confidenceScore", Math.Clamp(analysis.ConfidenceScore, 0, 100));
        resultCommand.Parameters.Add("analysisJson", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(analysis, JsonOptions);
        resultCommand.Parameters.AddWithValue("model", settings.Model);
        resultCommand.Parameters.AddWithValue("promptFingerprint", promptFingerprint);
        resultCommand.Parameters.AddWithValue("schemaVersion", AnalysisSchemaVersion);
        if (await resultCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"Conversation analysis execution '{analysisResultId}' is no longer processing.");
        }

        if (ShouldCreateCallSuggestion(recording, analysis))
        {
            await InsertCallSuggestionAsync(connection, transaction, recording, recordingId, analysis, settings, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static bool ShouldCreateCallSuggestion(MeetingAudioRecordingPayload recording, OpenAiMeetingAudioAnalysisResponse analysis) =>
        string.Equals(recording.SourceKind, "whatsapp_call", StringComparison.OrdinalIgnoreCase)
        && Guid.TryParse(recording.CompanyId, out _)
        && Guid.TryParse(recording.ContactId, out _)
        && analysis.ShouldCreateActivity
        && !string.IsNullOrWhiteSpace(analysis.ActivityTitle)
        && !string.IsNullOrWhiteSpace(analysis.ActivityNotes);

    private static async Task InsertCallSuggestionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MeetingAudioRecordingPayload recording,
        Guid recordingId,
        OpenAiMeetingAudioAnalysisResponse analysis,
        AiAgentRuntimeSettings settings,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into ai_agent_suggestions
                (id, company_id, agent_key, suggestion_type, status, contact_id, conversation_id, run_id,
                 title, description, suggested_due_at, payload, generation_model, confidence_score,
                 generation_reasons, created_at, updated_at)
            values
                (gen_random_uuid(), @companyId, 'call-audio-analysis', 'activity', 'pending', @contactId, null, @runId,
                 @title, @description, @dueAt, @payload, @generationModel, @confidenceScore,
                 @generationReasons, now(), now())
            on conflict (run_id, suggestion_type) where run_id is not null do nothing;
            """;

        var dueAt = ParseUtcDateTime(analysis.ActivityDueAt);
        var payload = JsonSerializer.Serialize(new
        {
            activityType = "follow-up",
            channel = "call",
            notes = analysis.ActivityNotes!.Trim(),
            dueAt = dueAt?.ToString("O"),
            recordingId = recording.Id,
            activityId = recording.ActivityId,
            accountId = recording.AccountId,
            opportunityId = recording.OpportunityId,
            ownerUserId = recording.OwnerUserId,
            sourceKind = recording.SourceKind
        });

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
        command.Parameters.AddWithValue("contactId", Guid.Parse(recording.ContactId!));
        command.Parameters.AddWithValue("runId", recordingId);
        command.Parameters.AddWithValue("title", Truncate(analysis.ActivityTitle!.Trim(), 300));
        command.Parameters.AddWithValue("description", Truncate(analysis.ActivityNotes!.Trim(), 3000));
        command.Parameters.Add("dueAt", NpgsqlDbType.TimestampTz).Value = dueAt?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.AddWithValue("generationModel", settings.Model);
        command.Parameters.AddWithValue("confidenceScore", Math.Clamp(analysis.ConfidenceScore, 0, 100));
        command.Parameters.Add("generationReasons", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(analysis.Reasons ?? []);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset? ParseUtcDateTime(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

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

    private static Guid? GetGuid(IReadOnlyDictionary<string, object?> data, string key) =>
        Guid.TryParse(GetString(data, key), out var value) ? value : null;

    private static bool GetBoolean(IReadOnlyDictionary<string, object?> data, string key) =>
        bool.TryParse(GetString(data, key), out var value) && value;

    internal static string PromptFingerprint(AiAgentRuntimeSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Instructions)));

    private static void AddNullableGuid(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = Guid.TryParse(value, out var parsed) ? parsed : DBNull.Value;

    private static string? ReadNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? ReadNullableGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal).ToString();
}
