using System.Security.Cryptography;
using System.Globalization;
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
            var forceRetranscription = GetBoolean(opportunityEvent.Data, "forceRetranscription");
            if (string.IsNullOrWhiteSpace(transcript) || forceRetranscription)
            {
                await UpdateStatusAsync(parsedRecordingId, "transcribing", null, cancellationToken);
                var transcription = await openAiClient.TranscribeAsync(settings, recording.FileName, recording.MimeType, recording.Content, invocationContext, cancellationToken);
                transcript = await PersistTranscriptionVersionAsync(recording, parsedRecordingId, transcription, cancellationToken);
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

            var selectedContext = await LoadSelectedContextAsync(recording, settings.ContextEntityKeys, cancellationToken);
            var scorecardTemplate = await LoadScorecardTemplateAsync(recording, GetGuid(opportunityEvent.Data, "scorecardTemplateId"), cancellationToken);
            var availableTags = await LoadAvailableTagsAsync(recording, cancellationToken);
            var availableContactFields = await LoadAvailableContactFieldsAsync(recording, cancellationToken);
            var analysis = await openAiClient.AnalyzeAsync(settings, new MeetingAudioAnalysisInput(
                transcript,
                settings.ContextEntityKeys.Contains("opportunity") ? recording.OpportunityName : null,
                settings.ContextEntityKeys.Contains("account") ? recording.AccountName : null,
                settings.ContextEntityKeys.Contains("activities") ? recording.ActivityTitle : null,
                settings.ContextEntityKeys.Contains("activities") ? recording.ActivityNotes : null,
                selectedContext.Notes,
                selectedContext.Contacts,
                selectedContext.Activities,
                selectedContext.AgentInsights,
                scorecardTemplate is null ? null : new MeetingScorecardTemplateInput(
                    scorecardTemplate.Id.ToString(), scorecardTemplate.Name, scorecardTemplate.Version,
                    scorecardTemplate.Criteria.Select(criterion => new MeetingScorecardCriterionInput(
                        criterion.Key, criterion.Title, criterion.Description, criterion.Weight,
                        criterion.EvaluationInstruction, criterion.PositiveExamples, criterion.NegativeExamples,
                        criterion.ScoreMin, criterion.ScoreMax, criterion.IsRequired)).ToArray()),
                availableTags,
                availableContactFields), invocationContext, cancellationToken);

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
                scorecardTemplate,
                availableTags,
                availableContactFields,
                cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            if (analysisResultId.HasValue)
            {
                await MarkAnalysisFailedBestEffortAsync(analysisResultId.Value, exception.Message);
            }
            await MarkCurrentTranscriptionAnalysisFailedBestEffortAsync(parsedRecordingId);
            await UpdateStatusAsync(parsedRecordingId, "failed", exception.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task MarkCurrentTranscriptionAnalysisFailedBestEffortAsync(Guid recordingId)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand("""
                update meeting_audio_transcription_versions
                set analysis_status = 'failed', updated_at = now()
                where recording_id = @recordingId and is_current;
                """, connection);
            command.Parameters.AddWithValue("recordingId", recordingId);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch
        {
            // Preserva a falha original do processamento.
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

    private async Task<MeetingTagOptionInput[]> LoadAvailableTagsAsync(
        MeetingAudioRecordingPayload recording,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(recording.CompanyId, out var companyId)) return [];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select id::text, name, description
            from tags
            where company_id = @companyId and status = 'active'
            order by name, id
            limit 200;
            """, connection);
        command.Parameters.AddWithValue("companyId", companyId);
        var values = new List<MeetingTagOptionInput>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new MeetingTagOptionInput(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return values.ToArray();
    }

    private async Task<MeetingContactFieldOptionInput[]> LoadAvailableContactFieldsAsync(
        MeetingAudioRecordingPayload recording,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(recording.CompanyId, out var companyId)
            || !Guid.TryParse(recording.ContactId, out var contactId))
            return [];

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select definition.id::text, definition.label, definition.field_type,
                   definition.options_json::text, value.value_text
            from contact_custom_field_definitions definition
            left join contact_custom_field_values value
              on value.field_id = definition.id
             and value.contact_id = @contactId
             and value.company_id = definition.company_id
            where definition.company_id = @companyId
              and definition.entity_type = 'contact'
              and definition.is_active = true
            order by definition.sort_order, definition.label, definition.id
            limit 100;
            """, connection);
        command.Parameters.AddWithValue("companyId", companyId);
        command.Parameters.AddWithValue("contactId", contactId);
        var fields = new List<MeetingContactFieldOptionInput>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            fields.Add(new MeetingContactFieldOptionInput(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseStringArray(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return fields.ToArray();
    }

    private async Task<ScorecardTemplate?> LoadScorecardTemplateAsync(MeetingAudioRecordingPayload recording, Guid? requestedTemplateId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(recording.CompanyId, out var companyId)) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string templateSql = """
            select template.id, template.template_key, template.version, template.name
            from conversation_scorecard_templates template
            where template.company_id = @companyId and template.status = 'published'
              and (template.valid_from is null or template.valid_from <= now())
              and (template.valid_to is null or template.valid_to > now())
              and (@requestedTemplateId is null or template.id = @requestedTemplateId)
              and (template.source_kind is null or template.source_kind = @sourceKind)
              and (template.pipeline_id is null or template.pipeline_id = @pipelineId)
              and (template.stage_id is null or template.stage_id = @stageId)
              and (template.group_id is null or template.group_id = @groupId)
              and (template.activity_type is null or template.activity_type = @activityType)
            order by
              case when @requestedTemplateId is not null and template.id = @requestedTemplateId then 1 else 0 end desc,
              template.priority desc,
              ((template.pipeline_id is not null)::int + (template.stage_id is not null)::int
               + (template.group_id is not null)::int + (template.activity_type is not null)::int
               + (template.source_kind is not null)::int) desc,
              template.version desc, template.published_at desc
            limit 1
            """;
        Guid id;
        Guid templateKey;
        int version;
        string name;
        await using (var command = new NpgsqlCommand(templateSql, connection))
        {
            command.Parameters.AddWithValue("companyId", companyId);
            command.Parameters.Add("requestedTemplateId", NpgsqlDbType.Uuid).Value = requestedTemplateId ?? (object)DBNull.Value;
            command.Parameters.AddWithValue("sourceKind", recording.SourceKind);
            AddNullableGuid(command, "pipelineId", recording.PipelineId);
            AddNullableGuid(command, "stageId", recording.StageId);
            AddNullableGuid(command, "groupId", recording.GroupId);
            command.Parameters.Add("activityType", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(recording.ActivityType) ? DBNull.Value : recording.ActivityType;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                if (requestedTemplateId.HasValue) throw new InvalidOperationException("O template de scorecard solicitado nao e compativel com a gravacao.");
                return null;
            }
            id = reader.GetGuid(0);
            templateKey = reader.GetGuid(1);
            version = reader.GetInt32(2);
            name = reader.GetString(3);
        }

        const string criteriaSql = """
            select id, criterion_key, title, description, weight, evaluation_instruction,
                   positive_examples::text, negative_examples::text,
                   score_min, score_max, is_required
            from conversation_scorecard_criteria
            where template_id = @templateId
            order by position, title
            """;
        var criteria = new List<ScorecardCriterion>();
        await using (var command = new NpgsqlCommand(criteriaSql, connection))
        {
            command.Parameters.AddWithValue("templateId", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                criteria.Add(new ScorecardCriterion(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), ReadNullableString(reader, 3),
                    reader.GetDecimal(4), reader.GetString(5), ParseStringArray(reader.GetString(6)),
                    ParseStringArray(reader.GetString(7)), reader.GetInt32(8), reader.GetInt32(9), reader.GetBoolean(10)));
            }
        }

        return criteria.Count == 0 ? null : new ScorecardTemplate(id, templateKey, version, name, criteria);
    }

    private static string[] ParseStringArray(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value, JsonOptions) ?? []; }
        catch { return []; }
    }

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
                owner.id as owner_user_id,
                mar.transcript,
                o.pipeline_id,
                o.stage_id,
                owner.group_id,
                act.activity_type,
                current_version.id as current_version_id
            from meeting_audio_recordings mar
            left join opportunities o on o.id = mar.opportunity_id and o.company_id = mar.company_id
            left join accounts a on a.id = mar.account_id and a.company_id = mar.company_id
            left join activities act on act.id = mar.activity_id and act.company_id = mar.company_id
            left join contacts contact on contact.id = act.contact_id and contact.company_id = mar.company_id
            left join users owner on owner.id = act.owner_user_id and owner.company_id = mar.company_id
            left join lateral (
                select version.id
                from meeting_audio_transcription_versions version
                where version.recording_id = mar.id and version.company_id = mar.company_id and version.is_current
                limit 1
            ) current_version on true
            where mar.id = @recordingId and mar.company_id is not null
              and (mar.opportunity_id is null or o.id is not null)
              and (mar.account_id is null or a.id is not null)
              and (mar.activity_id is null or act.id is not null)
              and (act.contact_id is null or contact.id is not null)
              and (act.owner_user_id is null or owner.id is not null)
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
            ReadNullableString(reader, 16),
            ReadNullableGuid(reader, 17),
            ReadNullableGuid(reader, 18),
            ReadNullableGuid(reader, 19),
            ReadNullableString(reader, 20),
            ReadNullableGuid(reader, 21));
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

    private async Task<string> PersistTranscriptionVersionAsync(
        MeetingAudioRecordingPayload recording,
        Guid recordingId,
        MeetingAudioTranscriptionResult transcription,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(recording.CompanyId, out var companyId))
        {
            throw new InvalidOperationException("A gravacao nao possui empresa valida para versionar a transcricao.");
        }

        var labels = transcription.Segments
            .Select(segment => segment.SpeakerLabel.Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((label, index) => new { Original = label, Normalized = SpeakerLabel(index) })
            .ToDictionary(item => item.Original, item => item.Normalized, StringComparer.OrdinalIgnoreCase);
        var normalizedSegments = transcription.Segments
            .Select((segment, index) => segment with
            {
                Id = string.IsNullOrWhiteSpace(segment.Id) ? $"segment-{index + 1}" : segment.Id,
                SpeakerLabel = labels.TryGetValue(segment.SpeakerLabel.Trim(), out var label) ? label : "A",
                StartMs = Math.Max(0, segment.StartMs),
                EndMs = Math.Max(Math.Max(0, segment.StartMs), segment.EndMs),
                Text = segment.Text.Trim()
            })
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.StartMs)
            .ThenBy(segment => segment.EndMs)
            .ToArray();
        var consolidatedTranscript = normalizedSegments.Length > 0
            ? FormatDiarizedTranscript(normalizedSegments)
            : transcription.Text.Trim();
        if (string.IsNullOrWhiteSpace(consolidatedTranscript))
        {
            return string.Empty;
        }

        var source = normalizedSegments.Length > 0 ? "openai_diarization" : "openai_plain";
        var versionId = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var currentVersionCommand = new NpgsqlCommand("""
            select source, transcript
            from meeting_audio_transcription_versions
            where recording_id = @recordingId and company_id = @companyId and is_current
            for update;
            """, connection, transaction))
        {
            currentVersionCommand.Parameters.AddWithValue("recordingId", recordingId);
            currentVersionCommand.Parameters.AddWithValue("companyId", companyId);
            await using var reader = await currentVersionCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)
                && string.Equals(reader.GetString(0), "google_meet", StringComparison.OrdinalIgnoreCase))
            {
                var googleTranscript = reader.GetString(1);
                await reader.DisposeAsync();
                await transaction.RollbackAsync(cancellationToken);
                return googleTranscript;
            }
        }
        await using (var supersede = new NpgsqlCommand("""
            update meeting_audio_transcription_versions
            set is_current = false, superseded_at = now(), updated_at = now()
            where recording_id = @recordingId and company_id = @companyId and is_current;
            """, connection, transaction))
        {
            supersede.Parameters.AddWithValue("recordingId", recordingId);
            supersede.Parameters.AddWithValue("companyId", companyId);
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var versionCommand = new NpgsqlCommand("""
            insert into meeting_audio_transcription_versions
                (id, company_id, recording_id, source, transcript, identification_status, analysis_status, is_current)
            values
                (@id, @companyId, @recordingId, @source, @transcript, 'unidentified', 'provisional', true);
            """, connection, transaction))
        {
            versionCommand.Parameters.AddWithValue("id", versionId);
            versionCommand.Parameters.AddWithValue("companyId", companyId);
            versionCommand.Parameters.AddWithValue("recordingId", recordingId);
            versionCommand.Parameters.AddWithValue("source", source);
            versionCommand.Parameters.AddWithValue("transcript", consolidatedTranscript);
            await versionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var speaker in normalizedSegments.GroupBy(segment => segment.SpeakerLabel, StringComparer.OrdinalIgnoreCase))
        {
            var sample = speaker.OrderByDescending(segment => segment.EndMs - segment.StartMs).First();
            await using var speakerCommand = new NpgsqlCommand("""
                insert into meeting_audio_transcription_speakers
                    (id, company_id, version_id, speaker_label, display_name, role, identity_kind, sample_start_ms, sample_end_ms)
                values
                    (gen_random_uuid(), @companyId, @versionId, @speakerLabel, @displayName, 'unknown', 'unknown', @sampleStartMs, @sampleEndMs);
                """, connection, transaction);
            speakerCommand.Parameters.AddWithValue("companyId", companyId);
            speakerCommand.Parameters.AddWithValue("versionId", versionId);
            speakerCommand.Parameters.AddWithValue("speakerLabel", speaker.Key);
            speakerCommand.Parameters.AddWithValue("displayName", $"Voz {speaker.Key}");
            speakerCommand.Parameters.AddWithValue("sampleStartMs", sample.StartMs);
            speakerCommand.Parameters.AddWithValue("sampleEndMs", sample.EndMs);
            await speakerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var position = 0; position < normalizedSegments.Length; position += 1)
        {
            var segment = normalizedSegments[position];
            await using var segmentCommand = new NpgsqlCommand("""
                insert into meeting_audio_transcription_segments
                    (id, company_id, version_id, position, speaker_label, start_ms, end_ms, text, provider_segment_id)
                values
                    (gen_random_uuid(), @companyId, @versionId, @position, @speakerLabel, @startMs, @endMs, @text, @providerSegmentId);
                """, connection, transaction);
            segmentCommand.Parameters.AddWithValue("companyId", companyId);
            segmentCommand.Parameters.AddWithValue("versionId", versionId);
            segmentCommand.Parameters.AddWithValue("position", position);
            segmentCommand.Parameters.AddWithValue("speakerLabel", segment.SpeakerLabel);
            segmentCommand.Parameters.AddWithValue("startMs", segment.StartMs);
            segmentCommand.Parameters.AddWithValue("endMs", segment.EndMs);
            segmentCommand.Parameters.AddWithValue("text", segment.Text);
            segmentCommand.Parameters.AddWithValue("providerSegmentId", NpgsqlDbType.Text, segment.Id);
            await segmentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var recordingCommand = new NpgsqlCommand("""
            update meeting_audio_recordings
            set transcript = @transcript, status = 'analyzing', processing_error = null, updated_at = now()
            where id = @recordingId and company_id = @companyId;
            """, connection, transaction))
        {
            recordingCommand.Parameters.AddWithValue("recordingId", recordingId);
            recordingCommand.Parameters.AddWithValue("companyId", companyId);
            recordingCommand.Parameters.AddWithValue("transcript", consolidatedTranscript);
            await recordingCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return consolidatedTranscript;
    }

    private static string FormatDiarizedTranscript(IEnumerable<MeetingAudioTranscriptionSegment> segments) =>
        string.Join('\n', segments.Select(segment =>
            $"[{FormatTimestamp(segment.StartMs)}] Voz {segment.SpeakerLabel} · Desconhecido: {segment.Text}"));

    private static string FormatTimestamp(int milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }

    private static string SpeakerLabel(int index)
    {
        var value = index + 1;
        var builder = new StringBuilder();
        while (value > 0)
        {
            value -= 1;
            builder.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }
        return builder.ToString();
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
        ScorecardTemplate? scorecardTemplate,
        IReadOnlyCollection<MeetingTagOptionInput> availableTags,
        IReadOnlyCollection<MeetingContactFieldOptionInput> availableContactFields,
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

        await using var versionCommand = new NpgsqlCommand("""
            update meeting_audio_transcription_versions
            set analysis_status = case when identification_status = 'complete' then 'ready' else 'provisional' end,
                updated_at = now()
            where recording_id = @recordingId and is_current;
            """, connection, transaction);
        versionCommand.Parameters.AddWithValue("recordingId", recordingId);
        await versionCommand.ExecuteNonQueryAsync(cancellationToken);

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

        if (scorecardTemplate is not null)
        {
            await InsertScorecardAsync(connection, transaction, recording, recordingId, analysisResultId,
                scorecardTemplate, analysis, transcript, settings.Model, promptFingerprint, cancellationToken);
        }

        if (ShouldCreateActivitySuggestion(recording, analysis))
        {
            await InsertActivitySuggestionAsync(connection, transaction, recording, recordingId, analysis, settings, promptFingerprint, cancellationToken);
        }

        if (ShouldCreateNoteSuggestion(recording, analysis, summary))
        {
            await InsertNoteSuggestionAsync(connection, transaction, recording, recordingId, analysis, summary, settings, promptFingerprint, cancellationToken);
        }

        var tagSuggestions = ValidateTagSuggestions(availableTags, analysis.SuggestedTags ?? [], transcript);
        if (tagSuggestions.Count > 0)
        {
            await InsertTagSuggestionAsync(connection, transaction, recording, recordingId, analysis, tagSuggestions, settings, promptFingerprint, cancellationToken);
        }

        var contactFieldSuggestions = ValidateContactFieldSuggestions(
            availableContactFields,
            analysis.SuggestedContactFields ?? [],
            transcript);
        if (contactFieldSuggestions.Count > 0)
        {
            await InsertContactFieldSuggestionAsync(
                connection, transaction, recording, recordingId, analysis,
                availableContactFields, contactFieldSuggestions, settings, promptFingerprint, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertScorecardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MeetingAudioRecordingPayload recording,
        Guid recordingId,
        Guid analysisResultId,
        ScorecardTemplate template,
        OpenAiMeetingAudioAnalysisResponse analysis,
        string transcript,
        string model,
        string promptFingerprint,
        CancellationToken cancellationToken)
    {
        var items = NormalizeScorecardItems(template, analysis.ScorecardItems ?? [], transcript);
        var coveredItems = items.Where(item => item.IsCovered).ToArray();
        var coveredWeight = coveredItems.Sum(item => item.Criterion.Weight);
        var overallScore = coveredWeight <= 0
            ? 0m
            : Math.Round(coveredItems.Sum(item => item.Score * item.Criterion.Weight) / coveredWeight, 2);
        var overallConfidence = coveredWeight <= 0
            ? 0
            : Math.Clamp((int)Math.Round(coveredItems.Sum(item => item.Confidence * item.Criterion.Weight) / coveredWeight), 0, 100);
        var scorecardId = Guid.NewGuid();

        await using (var supersedeCommand = new NpgsqlCommand("""
            update conversation_scorecards
            set is_current = false, updated_at = now()
            where company_id = @companyId
              and recording_id = @recordingId
              and is_current;
            """, connection, transaction))
        {
            supersedeCommand.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
            supersedeCommand.Parameters.AddWithValue("recordingId", recordingId);
            await supersedeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string scorecardSql = """
            insert into conversation_scorecards
                (id, company_id, analysis_result_id, recording_id, activity_id, source_kind, is_current, template_id, template_key,
                 template_version, evaluated_user_id, group_id, ai_score, status, confidence_score,
                 model, prompt_fingerprint)
            values
                (@id, @companyId, @analysisResultId, @recordingId, @activityId, @sourceKind, true, @templateId, @templateKey,
                 @templateVersion, @evaluatedUserId, @groupId, @aiScore, 'generated', @confidenceScore,
                 @model, @promptFingerprint)
            """;
        await using (var command = new NpgsqlCommand(scorecardSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", scorecardId);
            command.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
            command.Parameters.AddWithValue("analysisResultId", analysisResultId);
            command.Parameters.AddWithValue("recordingId", recordingId);
            AddNullableGuid(command, "activityId", recording.ActivityId);
            command.Parameters.AddWithValue("sourceKind", recording.SourceKind);
            command.Parameters.AddWithValue("templateId", template.Id);
            command.Parameters.AddWithValue("templateKey", template.TemplateKey);
            command.Parameters.AddWithValue("templateVersion", template.Version);
            AddNullableGuid(command, "evaluatedUserId", recording.OwnerUserId);
            AddNullableGuid(command, "groupId", recording.GroupId);
            command.Parameters.AddWithValue("aiScore", overallScore);
            command.Parameters.AddWithValue("confidenceScore", overallConfidence);
            command.Parameters.AddWithValue("model", model);
            command.Parameters.AddWithValue("promptFingerprint", promptFingerprint);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string itemSql = """
            insert into conversation_scorecard_items
                (id, scorecard_id, criterion_id, criterion_key, criterion_title, weight,
                 ai_score, confidence_score, justification, recommendation, evidence_json)
            values
                (gen_random_uuid(), @scorecardId, @criterionId, @criterionKey, @criterionTitle, @weight,
                 @score, @confidence, @justification, @recommendation, @evidenceJson)
            """;
        foreach (var item in items)
        {
            await using var command = new NpgsqlCommand(itemSql, connection, transaction);
            command.Parameters.AddWithValue("scorecardId", scorecardId);
            command.Parameters.AddWithValue("criterionId", item.Criterion.Id);
            command.Parameters.AddWithValue("criterionKey", item.Criterion.Key);
            command.Parameters.AddWithValue("criterionTitle", item.Criterion.Title);
            command.Parameters.AddWithValue("weight", item.Criterion.Weight);
            command.Parameters.AddWithValue("score", item.Score);
            command.Parameters.AddWithValue("confidence", item.Confidence);
            command.Parameters.AddWithValue("justification", item.Justification);
            command.Parameters.Add("recommendation", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(item.Recommendation) ? DBNull.Value : item.Recommendation;
            command.Parameters.Add("evidenceJson", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(item.Evidence, JsonOptions);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static IReadOnlyCollection<NormalizedScorecardItem> NormalizeScorecardItems(
        ScorecardTemplate template,
        IReadOnlyCollection<OpenAiConversationScorecardItem> modelItems,
        string transcript)
    {
        var byKey = modelItems
            .Where(item => !string.IsNullOrWhiteSpace(item.CriterionKey))
            .GroupBy(item => item.CriterionKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var normalizedTranscript = NormalizeWhitespace(transcript);
        return template.Criteria.Select(criterion =>
        {
            if (!byKey.TryGetValue(criterion.Key, out var modelItem))
            {
                return new NormalizedScorecardItem(criterion, criterion.ScoreMin, 0,
                    "Sem cobertura suficiente para avaliar este criterio.", null, [], false);
            }

            var evidence = (modelItem.Evidence ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Excerpt))
                .Where(item => normalizedTranscript.Contains(NormalizeWhitespace(item.Excerpt), StringComparison.OrdinalIgnoreCase))
                .Select(item => new NormalizedEvidence(
                    Truncate(item.Excerpt.Trim(), 500),
                    string.IsNullOrWhiteSpace(item.Participant) ? null : Truncate(item.Participant.Trim(), 120),
                    null,
                    null,
                    "transcript",
                    Math.Clamp(item.ConfidenceScore, 0, 100)))
                .DistinctBy(item => item.Excerpt, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();
            var covered = evidence.Length > 0;
            return new NormalizedScorecardItem(
                criterion,
                Math.Clamp(modelItem.Score, criterion.ScoreMin, criterion.ScoreMax),
                covered ? Math.Clamp(modelItem.ConfidenceScore, 0, 100) : 0,
                covered
                    ? Truncate(string.IsNullOrWhiteSpace(modelItem.Justification) ? "Avaliacao sustentada pela evidencia registrada." : modelItem.Justification.Trim(), 1500)
                    : "Sem cobertura suficiente: nenhuma evidencia literal valida foi localizada na transcricao.",
                string.IsNullOrWhiteSpace(modelItem.Recommendation) ? null : Truncate(modelItem.Recommendation.Trim(), 1000),
                evidence,
                covered);
        }).ToArray();
    }

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool ShouldCreateActivitySuggestion(MeetingAudioRecordingPayload recording, OpenAiMeetingAudioAnalysisResponse analysis) =>
        (string.Equals(recording.SourceKind, "google_meet", StringComparison.OrdinalIgnoreCase)
         || string.Equals(recording.SourceKind, "whatsapp_call", StringComparison.OrdinalIgnoreCase))
        && Guid.TryParse(recording.CompanyId, out _)
        && Guid.TryParse(recording.ContactId, out _)
        && analysis.ShouldCreateActivity
        && !string.IsNullOrWhiteSpace(analysis.ActivityTitle)
        && !string.IsNullOrWhiteSpace(analysis.ActivityNotes);

    public static bool ShouldCreateNoteSuggestion(
        MeetingAudioRecordingPayload recording,
        OpenAiMeetingAudioAnalysisResponse analysis,
        string summary) =>
        (string.Equals(recording.SourceKind, "google_meet", StringComparison.OrdinalIgnoreCase)
         || string.Equals(recording.SourceKind, "whatsapp_call", StringComparison.OrdinalIgnoreCase))
        && Guid.TryParse(recording.CompanyId, out _)
        && Guid.TryParse(recording.ContactId, out _)
        && !string.IsNullOrWhiteSpace(summary)
        && analysis.ConfidenceScore >= 60;

    private static async Task InsertActivitySuggestionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MeetingAudioRecordingPayload recording,
        Guid recordingId,
        OpenAiMeetingAudioAnalysisResponse analysis,
        AiAgentRuntimeSettings settings,
        string promptFingerprint,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into ai_agent_suggestions
                (id, company_id, agent_key, suggestion_type, status, contact_id, conversation_id, run_id,
                 title, description, suggested_due_at, payload, generation_model, confidence_score,
                 prompt_fingerprint, generation_reasons, created_at, updated_at)
            values
                (gen_random_uuid(), @companyId, @agentKey, 'activity', 'pending', @contactId, null, @runId,
                 @title, @description, @dueAt, @payload, @generationModel, @confidenceScore,
                 @promptFingerprint, @generationReasons, now(), now())
            on conflict (run_id, suggestion_type) where run_id is not null do nothing;
            """;

        var dueAt = ParseUtcDateTime(analysis.ActivityDueAt);
        var channel = string.Equals(recording.SourceKind, "whatsapp_call", StringComparison.OrdinalIgnoreCase)
            ? "call"
            : "meeting";
        var payload = JsonSerializer.Serialize(new
        {
            activityType = "follow-up",
            channel,
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
        command.Parameters.AddWithValue("agentKey", ResolveAgentKey(recording.SourceKind));
        command.Parameters.AddWithValue("contactId", Guid.Parse(recording.ContactId!));
        command.Parameters.AddWithValue("runId", recordingId);
        command.Parameters.AddWithValue("title", Truncate(analysis.ActivityTitle!.Trim(), 300));
        command.Parameters.AddWithValue("description", Truncate(analysis.ActivityNotes!.Trim(), 3000));
        command.Parameters.Add("dueAt", NpgsqlDbType.TimestampTz).Value = dueAt?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.AddWithValue("generationModel", settings.Model);
        command.Parameters.AddWithValue("confidenceScore", Math.Clamp(analysis.ConfidenceScore, 0, 100));
        command.Parameters.AddWithValue("promptFingerprint", promptFingerprint);
        command.Parameters.Add("generationReasons", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(analysis.Reasons ?? []);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertNoteSuggestionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MeetingAudioRecordingPayload recording,
        Guid recordingId,
        OpenAiMeetingAudioAnalysisResponse analysis,
        string summary,
        AiAgentRuntimeSettings settings,
        string promptFingerprint,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into ai_agent_suggestions
                (id, company_id, agent_key, suggestion_type, status, contact_id, conversation_id, run_id,
                 title, description, suggested_due_at, payload, generation_model, prompt_fingerprint,
                 confidence_score, generation_reasons, created_at, updated_at)
            values
                (gen_random_uuid(), @companyId, @agentKey, 'note', 'pending', @contactId, null, @runId,
                 @title, @description, null, @payload, @generationModel, @promptFingerprint,
                 @confidenceScore, @generationReasons, now(), now())
            on conflict (run_id, suggestion_type) where run_id is not null do nothing;
            """;

        const string targetType = "contact";
        var targetId = recording.ContactId!;
        var noteText = Truncate(summary.Trim(), 10_000);
        var sourceLabel = string.Equals(recording.SourceKind, "whatsapp_call", StringComparison.OrdinalIgnoreCase)
            ? "ligacao"
            : "reuniao";
        var payload = JsonSerializer.Serialize(new
        {
            targetType,
            targetId,
            text = noteText,
            recordingId = recording.Id,
            activityId = recording.ActivityId,
            opportunityId = recording.OpportunityId,
            contactId = recording.ContactId,
            sourceKind = recording.SourceKind
        });

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
        command.Parameters.AddWithValue("agentKey", ResolveAgentKey(recording.SourceKind));
        command.Parameters.AddWithValue("contactId", Guid.Parse(recording.ContactId!));
        command.Parameters.AddWithValue("runId", recordingId);
        command.Parameters.AddWithValue("title", $"Registrar resumo da {sourceLabel}");
        command.Parameters.AddWithValue("description", Truncate(noteText, 3000));
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.AddWithValue("generationModel", settings.Model);
        command.Parameters.AddWithValue("promptFingerprint", promptFingerprint);
        command.Parameters.AddWithValue("confidenceScore", Math.Clamp(analysis.ConfidenceScore, 0, 100));
        command.Parameters.Add("generationReasons", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(analysis.Reasons ?? []);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static IReadOnlyCollection<OpenAiConversationTagSuggestion> ValidateTagSuggestions(
        IReadOnlyCollection<MeetingTagOptionInput> availableTags,
        IReadOnlyCollection<OpenAiConversationTagSuggestion> suggestions,
        string transcript)
    {
        var availableIds = availableTags
            .Select(tag => Guid.TryParse(tag.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var normalizedTranscript = NormalizeWhitespace(transcript);
        var selected = new List<OpenAiConversationTagSuggestion>();
        var selectedIds = new HashSet<Guid>();
        foreach (var suggestion in suggestions)
        {
            if (!Guid.TryParse(suggestion.TagId, out var tagId)
                || !availableIds.Contains(tagId)
                || !selectedIds.Add(tagId)
                || string.IsNullOrWhiteSpace(suggestion.Reason)
                || string.IsNullOrWhiteSpace(suggestion.EvidenceExcerpt)
                || !normalizedTranscript.Contains(
                    NormalizeWhitespace(suggestion.EvidenceExcerpt),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            selected.Add(suggestion with { TagId = tagId.ToString() });
            if (selected.Count == 5) break;
        }
        return selected;
    }

    private static async Task InsertTagSuggestionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MeetingAudioRecordingPayload recording,
        Guid recordingId,
        OpenAiMeetingAudioAnalysisResponse analysis,
        IReadOnlyCollection<OpenAiConversationTagSuggestion> suggestions,
        AiAgentRuntimeSettings settings,
        string promptFingerprint,
        CancellationToken cancellationToken)
    {
        var requestedIds = suggestions.Select(suggestion => Guid.Parse(suggestion.TagId)).ToArray();
        var activeTags = new List<(Guid Id, string Name)>();
        await using (var lookup = new NpgsqlCommand("""
            select tag.id, tag.name
            from tags tag
            where tag.company_id = @companyId
              and tag.status = 'active'
              and tag.id = any(@tagIds)
              and not exists (
                  select 1 from entity_tags current
                  where current.entity_type = 'contact'
                    and current.entity_id = @contactId
                    and current.tag_id = tag.id
              )
            order by tag.name, tag.id;
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
            lookup.Parameters.AddWithValue("contactId", Guid.Parse(recording.ContactId!));
            lookup.Parameters.AddWithValue("tagIds", requestedIds);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                activeTags.Add((reader.GetGuid(0), reader.GetString(1)));
        }
        if (activeTags.Count == 0) return;

        var evidenceById = suggestions.ToDictionary(suggestion => Guid.Parse(suggestion.TagId));
        var selected = activeTags.Select(tag => new
        {
            id = tag.Id,
            name = tag.Name,
            reason = evidenceById[tag.Id].Reason.Trim(),
            evidenceExcerpt = evidenceById[tag.Id].EvidenceExcerpt.Trim()
        }).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            targetType = "contact",
            targetId = recording.ContactId,
            tagIds = selected.Select(tag => tag.id).ToArray(),
            tags = selected,
            recordingId = recording.Id,
            sourceKind = recording.SourceKind
        });

        await using var command = new NpgsqlCommand("""
            insert into ai_agent_suggestions
                (id, company_id, agent_key, suggestion_type, status, contact_id, conversation_id, run_id,
                 title, description, suggested_due_at, payload, generation_model, prompt_fingerprint,
                 confidence_score, generation_reasons, created_at, updated_at)
            values
                (gen_random_uuid(), @companyId, @agentKey, 'tags', 'pending', @contactId, null, @runId,
                 'Adicionar tags identificadas', @description, null, @payload, @generationModel, @promptFingerprint,
                 @confidenceScore, @generationReasons, now(), now())
            on conflict (run_id, suggestion_type) where run_id is not null do nothing;
            """, connection, transaction);
        command.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
        command.Parameters.AddWithValue("agentKey", ResolveAgentKey(recording.SourceKind));
        command.Parameters.AddWithValue("contactId", Guid.Parse(recording.ContactId!));
        command.Parameters.AddWithValue("runId", recordingId);
        command.Parameters.AddWithValue("description", "Adicionar ao contato: " + string.Join(", ", selected.Select(tag => tag.name)));
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.AddWithValue("generationModel", settings.Model);
        command.Parameters.AddWithValue("promptFingerprint", promptFingerprint);
        command.Parameters.AddWithValue("confidenceScore", Math.Clamp(analysis.ConfidenceScore, 0, 100));
        command.Parameters.Add("generationReasons", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(selected.Select(tag => tag.reason));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static IReadOnlyCollection<OpenAiConversationContactFieldSuggestion> ValidateContactFieldSuggestions(
        IReadOnlyCollection<MeetingContactFieldOptionInput> availableFields,
        IReadOnlyCollection<OpenAiConversationContactFieldSuggestion> suggestions,
        string transcript)
    {
        var fieldsById = new Dictionary<Guid, MeetingContactFieldOptionInput>();
        foreach (var field in availableFields)
        {
            if (Guid.TryParse(field.Id, out var fieldId) && !fieldsById.ContainsKey(fieldId))
                fieldsById[fieldId] = field;
        }

        var normalizedTranscript = NormalizeWhitespace(transcript);
        var selected = new List<OpenAiConversationContactFieldSuggestion>();
        var selectedIds = new HashSet<Guid>();
        foreach (var suggestion in suggestions)
        {
            if (!Guid.TryParse(suggestion.FieldId, out var fieldId)
                || !fieldsById.TryGetValue(fieldId, out var field)
                || !selectedIds.Add(fieldId)
                || string.IsNullOrWhiteSpace(suggestion.Reason)
                || string.IsNullOrWhiteSpace(suggestion.EvidenceExcerpt)
                || !normalizedTranscript.Contains(
                    NormalizeWhitespace(suggestion.EvidenceExcerpt),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var value = NormalizeSuggestedContactFieldValue(suggestion.Value, field.FieldType, field.Options);
            if (value is null || string.Equals(value, field.CurrentValue?.Trim(), StringComparison.Ordinal))
                continue;

            selected.Add(suggestion with
            {
                FieldId = fieldId.ToString(),
                Value = value,
                Reason = suggestion.Reason.Trim(),
                EvidenceExcerpt = suggestion.EvidenceExcerpt.Trim()
            });
            if (selected.Count == 5) break;
        }
        return selected;
    }

    private static string? NormalizeSuggestedContactFieldValue(
        string? value,
        string fieldType,
        IReadOnlyCollection<string> options)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 2000) return null;

        if (options.Count > 0)
        {
            var option = options.FirstOrDefault(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
            if (option is null) return null;
            normalized = option;
        }

        return fieldType.Trim().ToLowerInvariant() switch
        {
            "text" => normalized,
            "number" when decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                => number.ToString(CultureInfo.InvariantCulture),
            "date" when DateOnly.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "boolean" when bool.TryParse(normalized, out var boolean)
                => boolean.ToString().ToLowerInvariant(),
            _ => null
        };
    }

    private static async Task InsertContactFieldSuggestionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MeetingAudioRecordingPayload recording,
        Guid recordingId,
        OpenAiMeetingAudioAnalysisResponse analysis,
        IReadOnlyCollection<MeetingContactFieldOptionInput> availableFields,
        IReadOnlyCollection<OpenAiConversationContactFieldSuggestion> suggestions,
        AiAgentRuntimeSettings settings,
        string promptFingerprint,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(recording.ContactId, out var contactId)) return;

        var originalById = availableFields
            .Where(field => Guid.TryParse(field.Id, out _))
            .GroupBy(field => Guid.Parse(field.Id))
            .ToDictionary(group => group.Key, group => group.First());
        var suggestionById = suggestions.ToDictionary(suggestion => Guid.Parse(suggestion.FieldId));
        var requestedIds = suggestionById.Keys.ToArray();
        var selected = new List<PersistedContactFieldSuggestion>();
        await using (var lookup = new NpgsqlCommand("""
            select definition.id, definition.label, definition.field_type,
                   definition.options_json::text, value.value_text
            from contact_custom_field_definitions definition
            left join contact_custom_field_values value
              on value.field_id = definition.id
             and value.contact_id = @contactId
             and value.company_id = definition.company_id
            where definition.company_id = @companyId
              and definition.entity_type = 'contact'
              and definition.is_active = true
              and definition.id = any(@fieldIds)
            order by definition.sort_order, definition.label, definition.id;
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
            lookup.Parameters.AddWithValue("contactId", contactId);
            lookup.Parameters.AddWithValue("fieldIds", requestedIds);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var fieldId = reader.GetGuid(0);
                var currentValue = reader.IsDBNull(4) ? null : reader.GetString(4);
                if (!originalById.TryGetValue(fieldId, out var original)
                    || !string.Equals(original.CurrentValue, currentValue, StringComparison.Ordinal)
                    || !suggestionById.TryGetValue(fieldId, out var suggestion))
                    continue;

                var fieldType = reader.GetString(2);
                var value = NormalizeSuggestedContactFieldValue(
                    suggestion.Value, fieldType, ParseStringArray(reader.GetString(3)));
                if (value is null || string.Equals(value, currentValue?.Trim(), StringComparison.Ordinal))
                    continue;

                selected.Add(new PersistedContactFieldSuggestion(
                    fieldId, reader.GetString(1), fieldType, currentValue, value,
                    suggestion.Reason.Trim(), suggestion.EvidenceExcerpt.Trim()));
            }
        }
        if (selected.Count == 0) return;

        var payload = JsonSerializer.Serialize(new
        {
            targetType = "contact",
            targetId = recording.ContactId,
            fields = selected.Select(field => new
            {
                fieldId = field.Id,
                field.Label,
                field.FieldType,
                previousValue = field.PreviousValue,
                field.Value,
                field.Reason,
                field.EvidenceExcerpt
            }).ToArray(),
            recordingId = recording.Id,
            sourceKind = recording.SourceKind
        });

        await using var command = new NpgsqlCommand("""
            insert into ai_agent_suggestions
                (id, company_id, agent_key, suggestion_type, status, contact_id, conversation_id, run_id,
                 title, description, suggested_due_at, payload, generation_model, prompt_fingerprint,
                 confidence_score, generation_reasons, created_at, updated_at)
            values
                (gen_random_uuid(), @companyId, @agentKey, 'contact_fields', 'pending', @contactId, null, @runId,
                 'Atualizar campos personalizados identificados', @description, null, @payload, @generationModel,
                 @promptFingerprint, @confidenceScore, @generationReasons, now(), now())
            on conflict (run_id, suggestion_type) where run_id is not null do nothing;
            """, connection, transaction);
        command.Parameters.AddWithValue("companyId", Guid.Parse(recording.CompanyId!));
        command.Parameters.AddWithValue("agentKey", ResolveAgentKey(recording.SourceKind));
        command.Parameters.AddWithValue("contactId", contactId);
        command.Parameters.AddWithValue("runId", recordingId);
        command.Parameters.AddWithValue("description", string.Join("; ", selected.Select(field => $"{field.Label}: {field.Value}")));
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.AddWithValue("generationModel", settings.Model);
        command.Parameters.AddWithValue("promptFingerprint", promptFingerprint);
        command.Parameters.AddWithValue("confidenceScore", Math.Clamp(analysis.ConfidenceScore, 0, 100));
        command.Parameters.Add("generationReasons", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(selected.Select(field => field.Reason));
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

    internal sealed record ScorecardTemplate(Guid Id, Guid TemplateKey, int Version, string Name, IReadOnlyCollection<ScorecardCriterion> Criteria);
    internal sealed record ScorecardCriterion(Guid Id, string Key, string Title, string? Description, decimal Weight, string EvaluationInstruction, IReadOnlyCollection<string> PositiveExamples, IReadOnlyCollection<string> NegativeExamples, int ScoreMin, int ScoreMax, bool IsRequired);
    internal sealed record NormalizedEvidence(string Excerpt, string? Participant, int? StartMs, int? EndMs, string Source, int ConfidenceScore);
    internal sealed record NormalizedScorecardItem(ScorecardCriterion Criterion, int Score, int Confidence, string Justification, string? Recommendation, IReadOnlyCollection<NormalizedEvidence> Evidence, bool IsCovered);
    private sealed record PersistedContactFieldSuggestion(
        Guid Id,
        string Label,
        string FieldType,
        string? PreviousValue,
        string Value,
        string Reason,
        string EvidenceExcerpt);
}
