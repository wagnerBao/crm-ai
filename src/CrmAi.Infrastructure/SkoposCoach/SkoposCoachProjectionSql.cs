namespace CrmAi.Infrastructure.SkoposCoach;

internal static class SkoposCoachProjectionSql
{
    internal const string Whatsapp = """
        INSERT INTO skopos_coach_agent_reports
            (company_id, source_type, source_agent_key, source_entity_type, source_id, group_id,
             owner_user_id, opportunity_id, period_started_at, period_ended_at, occurred_at,
             report_summary, insights_json, severity, source_confidence, source_version, updated_at)
        SELECT run.company_id, 'whatsapp', 'whatsapp-conversation-analysis', 'conversation_analysis_run', run.id,
               owner.group_id, coalesce(opportunity.owner_user_id, conversation.owner_user_id), opportunity.id,
               coalesce(run.window_start_at, run.window_end_at), run.window_end_at, run.window_end_at,
               left(run.summary, 4000),
               jsonb_build_object('confidence', round(coalesce(insight.confidence, 0) * 100), 'analysisKind', insight.kind),
               null, round(coalesce(insight.confidence, 0) * 100)::int, run.updated_at, now()
        FROM whatsapp_conversation_analysis_runs run
        JOIN whatsapp_conversations conversation ON conversation.id = run.conversation_id
        LEFT JOIN LATERAL (
            SELECT candidate.* FROM opportunity_contacts link JOIN opportunities candidate ON candidate.id = link.opportunity_id
            WHERE link.contact_id = conversation.contact_id AND candidate.company_id = run.company_id
            ORDER BY candidate.updated_at DESC LIMIT 1
        ) opportunity ON true
        LEFT JOIN users owner ON owner.id = coalesce(opportunity.owner_user_id, conversation.owner_user_id)
        LEFT JOIN ai_insights insight ON insight.id = run.ai_insight_id
        WHERE run.status = 'completed' AND nullif(btrim(run.summary), '') IS NOT NULL
          AND run.window_end_at >= now() - interval '30 days' AND run.company_id IS NOT NULL
        ON CONFLICT (company_id, source_type, source_id) DO UPDATE SET
            source_agent_key = excluded.source_agent_key, source_entity_type = excluded.source_entity_type,
            group_id = excluded.group_id, owner_user_id = excluded.owner_user_id,
            opportunity_id = excluded.opportunity_id, period_started_at = excluded.period_started_at,
            period_ended_at = excluded.period_ended_at, occurred_at = excluded.occurred_at,
            report_summary = excluded.report_summary, insights_json = excluded.insights_json,
            severity = excluded.severity, source_confidence = excluded.source_confidence,
            source_version = excluded.source_version, updated_at = now()
        WHERE skopos_coach_agent_reports.source_version < excluded.source_version
        """;

    internal const string Meeting = """
        INSERT INTO skopos_coach_agent_reports
            (company_id, source_type, source_agent_key, source_entity_type, source_id, group_id,
             owner_user_id, opportunity_id, period_started_at, period_ended_at, occurred_at,
             report_summary, insights_json, source_version, updated_at)
        SELECT recording.company_id, 'meeting',
               CASE WHEN recording.source_kind = 'whatsapp_call' THEN 'call-audio-analysis' ELSE 'meeting-service-analysis' END,
               'meeting_recording', recording.id, owner.group_id,
               coalesce(activity.owner_user_id, opportunity.owner_user_id), recording.opportunity_id,
               recording.started_at, recording.ended_at, recording.ended_at, left(recording.summary, 4000),
               jsonb_build_object('status', recording.status, 'durationMinutes', round(recording.duration_ms / 60000.0, 1)),
               recording.updated_at, now()
        FROM meeting_audio_recordings recording
        LEFT JOIN activities activity ON activity.id = recording.activity_id
        LEFT JOIN opportunities opportunity ON opportunity.id = recording.opportunity_id
        LEFT JOIN users owner ON owner.id = coalesce(activity.owner_user_id, opportunity.owner_user_id)
        WHERE nullif(btrim(recording.summary), '') IS NOT NULL
          AND recording.ended_at >= now() - interval '30 days' AND recording.company_id IS NOT NULL
        ON CONFLICT (company_id, source_type, source_id) DO UPDATE SET
            source_agent_key = excluded.source_agent_key, source_entity_type = excluded.source_entity_type,
            group_id = excluded.group_id, owner_user_id = excluded.owner_user_id,
            opportunity_id = excluded.opportunity_id, period_started_at = excluded.period_started_at,
            period_ended_at = excluded.period_ended_at, occurred_at = excluded.occurred_at,
            report_summary = excluded.report_summary, insights_json = excluded.insights_json,
            source_version = excluded.source_version, updated_at = now()
        WHERE skopos_coach_agent_reports.source_version < excluded.source_version
        """;

    internal const string Risk = """
        INSERT INTO skopos_coach_agent_reports
            (company_id, source_type, source_agent_key, source_entity_type, source_id, group_id,
             owner_user_id, opportunity_id, period_started_at, period_ended_at, occurred_at,
             report_summary, insights_json, severity, source_confidence, source_version, updated_at)
        SELECT insight.company_id, 'opportunity_risk', 'risk-analysis', 'ai_insight', insight.id,
               owner.group_id, opportunity.owner_user_id, insight.opportunity_id,
               insight.created_at, insight.updated_at, insight.created_at, left(insight.message, 4000),
               jsonb_build_object('confidence', round(coalesce(insight.confidence, 0) * 100), 'status', insight.status),
               insight.status, round(coalesce(insight.confidence, 0) * 100)::int, insight.updated_at, now()
        FROM ai_insights insight
        JOIN opportunities opportunity ON opportunity.id = insight.opportunity_id
        LEFT JOIN users owner ON owner.id = opportunity.owner_user_id
        WHERE insight.kind IN ('risk', 'risk-analysis') AND insight.created_at >= now() - interval '30 days'
          AND insight.company_id IS NOT NULL
        ON CONFLICT (company_id, source_type, source_id) DO UPDATE SET
            source_agent_key = excluded.source_agent_key, source_entity_type = excluded.source_entity_type,
            group_id = excluded.group_id, owner_user_id = excluded.owner_user_id,
            opportunity_id = excluded.opportunity_id, period_started_at = excluded.period_started_at,
            period_ended_at = excluded.period_ended_at, occurred_at = excluded.occurred_at,
            report_summary = excluded.report_summary, insights_json = excluded.insights_json,
            severity = excluded.severity, source_confidence = excluded.source_confidence,
            source_version = excluded.source_version, updated_at = now()
        WHERE skopos_coach_agent_reports.source_version < excluded.source_version
        """;

    internal const string Checkout = """
        INSERT INTO skopos_coach_agent_reports
            (company_id, source_type, source_agent_key, source_entity_type, source_id, group_id,
             owner_user_id, period_started_at, period_ended_at, occurred_at, report_summary,
             insights_json, represented_evidence_count, source_version, updated_at)
        SELECT snapshot.company_id, 'daily_checkout', 'daily-checkout', 'daily_checkout_user_performance',
               (substr(md5(snapshot.id::text || ':' || performance->>'id'),1,8) || '-' ||
                substr(md5(snapshot.id::text || ':' || performance->>'id'),9,4) || '-' ||
                substr(md5(snapshot.id::text || ':' || performance->>'id'),13,4) || '-' ||
                substr(md5(snapshot.id::text || ':' || performance->>'id'),17,4) || '-' ||
                substr(md5(snapshot.id::text || ':' || performance->>'id'),21,12))::uuid,
               owner.group_id, owner.id, snapshot.snapshot_at, snapshot.snapshot_at, snapshot.snapshot_at,
               left(concat_ws(' ', 'Execução diária da equipe.', 'Planejado:', performance->>'planned',
                    'Realizado:', performance->>'executed', 'Percentual:', performance->>'percent'), 4000),
               jsonb_build_object('planned', performance->'planned', 'executed', performance->'executed',
                                  'credited', performance->'credited', 'percent', performance->'percent'),
               CASE WHEN coalesce(performance->>'goals', '') ~ '^[0-9]+$'
                    THEN greatest(1, (performance->>'goals')::int) ELSE 1 END,
               snapshot.updated_at, now()
        FROM daily_checkout_snapshots snapshot
        CROSS JOIN LATERAL jsonb_array_elements(coalesce(snapshot.payload_json #> '{tables,performance}', '[]'::jsonb)) performance
        JOIN users owner ON owner.company_id = snapshot.company_id
                        AND owner.id = CASE WHEN coalesce(performance->>'id','') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                                            THEN (performance->>'id')::uuid ELSE NULL END
        WHERE snapshot.snapshot_at >= now() - interval '30 days' AND snapshot.company_id IS NOT NULL
        ON CONFLICT (company_id, source_type, source_id) DO UPDATE SET
            source_agent_key = excluded.source_agent_key, source_entity_type = excluded.source_entity_type,
            group_id = excluded.group_id, owner_user_id = excluded.owner_user_id,
            period_started_at = excluded.period_started_at, period_ended_at = excluded.period_ended_at,
            occurred_at = excluded.occurred_at, report_summary = excluded.report_summary,
            insights_json = excluded.insights_json, represented_evidence_count = excluded.represented_evidence_count,
            source_version = excluded.source_version, updated_at = now()
        WHERE skopos_coach_agent_reports.source_version < excluded.source_version
        """;

    internal const string UpdateHealth = """
        WITH effective_settings AS (
            SELECT company.id company_id, coalesce(setting.is_active, false) is_active,
                   coalesce(setting.is_configured, false) is_configured
            FROM companies company
            LEFT JOIN LATERAL (
                SELECT bool_or(selected.is_active) is_active,
                       bool_or(nullif(btrim(selected.provider), '') IS NOT NULL
                           AND nullif(btrim(selected.model), '') IS NOT NULL
                           AND nullif(btrim(selected.api_key), '') IS NOT NULL) is_configured
                FROM (
                    SELECT DISTINCT ON (candidate.agent_key) candidate.*
                    FROM ai_agent_settings candidate
                    WHERE candidate.agent_key = ANY(@agentKeys)
                      AND (candidate.company_id = company.id OR candidate.company_id IS NULL)
                    ORDER BY candidate.agent_key, candidate.company_id NULLS LAST
                ) selected
            ) setting ON true
        ), reports AS (
            SELECT company_id, count(*)::int report_count, max(occurred_at) last_occurred_at
            FROM skopos_coach_agent_reports
            WHERE source_type = @sourceType AND occurred_at >= now() - interval '30 days'
            GROUP BY company_id
        )
        INSERT INTO skopos_coach_source_health
            (company_id, source_type, source_agent_key, status, is_configured, is_active,
             report_count, last_occurred_at, last_projected_at, error_message, updated_at)
        SELECT settings.company_id, @sourceType, @sourceAgentKey,
               CASE WHEN @error IS NOT NULL THEN 'error'
                    WHEN NOT settings.is_configured THEN 'unconfigured'
                    WHEN NOT settings.is_active THEN 'disabled'
                    WHEN coalesce(reports.report_count, 0) = 0 THEN 'healthy_no_events'
                    ELSE 'ready' END,
               settings.is_configured, settings.is_active, coalesce(reports.report_count, 0),
               reports.last_occurred_at, now(), @error, now()
        FROM effective_settings settings
        LEFT JOIN reports ON reports.company_id = settings.company_id
        ON CONFLICT (company_id, source_type) DO UPDATE SET
            source_agent_key = excluded.source_agent_key, status = excluded.status,
            is_configured = excluded.is_configured, is_active = excluded.is_active,
            report_count = excluded.report_count, last_occurred_at = excluded.last_occurred_at,
            last_projected_at = excluded.last_projected_at, error_message = excluded.error_message, updated_at = now()
        """;

    internal const string QueueDailyRuns = """
        WITH eligible AS (
            SELECT DISTINCT report.company_id, report.group_id, coalesce(report.group_id::text, 'all') scope_key,
                   CASE WHEN report.group_id IS NULL THEN '[]'::jsonb ELSE jsonb_build_array(report.group_id::text) END group_ids
            FROM skopos_coach_agent_reports report
            JOIN LATERAL (
                SELECT is_active FROM ai_agent_settings settings
                WHERE settings.agent_key = 'skopos-coach'
                  AND (settings.company_id = report.company_id OR settings.company_id IS NULL)
                ORDER BY settings.company_id NULLS LAST LIMIT 1
            ) settings ON settings.is_active
            WHERE report.source_type = 'daily_checkout' AND report.occurred_at::date = current_date
        )
        INSERT INTO skopos_coach_runs
            (company_id, trigger_type, status, date_from, date_to, group_ids, scope_key)
        SELECT company_id, 'scheduled', 'pending', current_date - 29, current_date, group_ids, scope_key
        FROM eligible candidate
        WHERE NOT EXISTS (
                SELECT 1 FROM skopos_coach_runs run
                WHERE run.company_id = candidate.company_id AND run.date_to = current_date
                  AND run.trigger_type IN ('daily_checkout', 'scheduled') AND run.scope_key = candidate.scope_key)
          AND NOT EXISTS (
                SELECT 1 FROM skopos_coach_runs active
                WHERE active.company_id = candidate.company_id AND active.status IN ('pending','processing')
                  AND (active.scope_key = 'all' OR candidate.scope_key = 'all' OR active.scope_key = candidate.scope_key))
        ON CONFLICT DO NOTHING
        """;

    internal const string ClaimRun = """
        UPDATE skopos_coach_runs run
        SET status = 'processing', started_at = now(), updated_at = now(), attempt_count = attempt_count + 1
        WHERE run.id = (SELECT pending.id FROM skopos_coach_runs pending WHERE pending.status = 'pending'
                        ORDER BY pending.created_at FOR UPDATE SKIP LOCKED LIMIT 1)
        RETURNING run.id, run.company_id, run.date_from, run.date_to, run.group_ids::text
        """;

    internal const string ReadReports = """
        SELECT id, source_type, source_id, group_id, owner_user_id, opportunity_id, occurred_at,
               report_summary, insights_json::text, represented_evidence_count
        FROM skopos_coach_agent_reports
        WHERE company_id = @companyId AND occurred_at >= @from::date AND occurred_at < (@to::date + interval '1 day')
          AND (@groups = '[]'::jsonb OR @groups ? group_id::text)
        ORDER BY occurred_at DESC LIMIT 2000
        """;

    internal const string ReadCoverage = """
        SELECT health.source_type, health.status, health.is_configured, health.is_active,
               count(report.id)::int report_count, max(report.occurred_at) last_occurred_at,
               health.last_projected_at, health.error_message
        FROM skopos_coach_source_health health
        LEFT JOIN skopos_coach_agent_reports report ON report.company_id = health.company_id
          AND report.source_type = health.source_type
          AND report.occurred_at >= @from::date AND report.occurred_at < (@to::date + interval '1 day')
          AND (@groups = '[]'::jsonb OR @groups ? report.group_id::text)
        WHERE health.company_id = @companyId
        GROUP BY health.source_type, health.status, health.is_configured, health.is_active,
                 health.last_projected_at, health.error_message
        ORDER BY health.source_type
        """;

    internal const string ReadCommercialContext = """
        SELECT jsonb_build_object(
          'products', (SELECT coalesce(jsonb_agg(item), '[]'::jsonb) FROM (
            SELECT product.name, count(*)::int opportunity_count
            FROM opportunity_products link JOIN products product ON product.id = link.product_id
            JOIN opportunities opportunity ON opportunity.id = link.opportunity_id
            LEFT JOIN users owner ON owner.id = opportunity.owner_user_id
            WHERE opportunity.company_id = @companyId AND opportunity.updated_at >= @from::date
              AND opportunity.updated_at < (@to::date + interval '1 day')
              AND (@groups = '[]'::jsonb OR @groups ? owner.group_id::text)
            GROUP BY product.name ORDER BY count(*) DESC LIMIT 10) item),
          'ads', (SELECT coalesce(jsonb_agg(item), '[]'::jsonb) FROM (
            SELECT source_platform, coalesce(campaign_name, utm_campaign, 'Sem campanha') campaign, count(*)::int leads
            FROM commercial_attribution_events WHERE company_id = @companyId AND captured_at >= @from::date
              AND captured_at < (@to::date + interval '1 day')
            GROUP BY source_platform, coalesce(campaign_name, utm_campaign, 'Sem campanha') ORDER BY count(*) DESC LIMIT 10) item),
          'metrics', (SELECT jsonb_build_object(
            'activities', count(*)::int,
            'completedActivities', count(*) FILTER (WHERE status = 'done')::int,
            'collaborators', count(DISTINCT owner_user_id)::int)
            FROM activities WHERE company_id = @companyId AND date_at >= @from::date AND date_at < (@to::date + interval '1 day')))
        """;

    internal const string FindTopic = """
        SELECT id, status FROM skopos_coach_training_topics
        WHERE company_id = @companyId AND group_id = @groupId AND gap_key = @gapKey
        ORDER BY last_detected_at DESC, created_at DESC LIMIT 1
        """;

    internal const string UpdateRecurringTopic = """
        UPDATE skopos_coach_training_topics
        SET last_run_id = @runId, recurrence_count = recurrence_count + 1,
            last_detected_at = now(), confidence = greatest(confidence, @confidence),
            impact_score = greatest(impact_score, @impact),
            objective = coalesce(objective, @objective),
            target_audience = coalesce(target_audience, @audience),
            training_format = coalesce(training_format, @format),
            duration_minutes = coalesce(duration_minutes, @duration),
            outline_json = CASE WHEN outline_json = '[]'::jsonb THEN @outline::jsonb ELSE outline_json END,
            recommended_action = coalesce(recommended_action, @action), updated_at = now()
        WHERE id = @id
        """;

    internal const string InsertTopic = """
        INSERT INTO skopos_coach_training_topics
            (id, company_id, run_id, last_run_id, previous_topic_id, group_id, gap_key,
             title, summary, category, objective, target_audience, priority, training_format,
             duration_minutes, outline_json, confidence, evidence_count, collaborator_count,
             impact_score, recommended_action)
        VALUES (@id, @companyId, @runId, @runId, @previous, @groupId, @gapKey,
                @title, @summary, @category, @objective, @audience, @priority, @format,
                @duration, @outline::jsonb, @confidence, @evidenceCount, @collaborators,
                @impact, @action)
        """;

    internal const string InsertEvidence = """
        INSERT INTO skopos_coach_topic_evidence (topic_id, report_id, relevance, excerpt)
        VALUES (@topicId, @reportId, @relevance, @excerpt) ON CONFLICT DO NOTHING
        """;

    internal const string RecountTopic = """
        UPDATE skopos_coach_training_topics topic
        SET evidence_count = aggregate.evidence_count,
            collaborator_count = aggregate.collaborator_count, updated_at = now()
        FROM (
            SELECT evidence.topic_id, count(*)::int evidence_count,
                   count(DISTINCT report.owner_user_id)::int collaborator_count
            FROM skopos_coach_topic_evidence evidence
            JOIN skopos_coach_agent_reports report ON report.id = evidence.report_id
            WHERE evidence.topic_id = @topicId GROUP BY evidence.topic_id
        ) aggregate
        WHERE topic.id = aggregate.topic_id
        """;

    internal const string CompleteRun = """
        UPDATE skopos_coach_runs SET status = @status, report_count = @reportCount,
            evidence_count = @evidenceCount, executive_summary = @summary,
            source_coverage_json = @coverage::jsonb, commercial_context_json = @context::jsonb,
            trends_json = @trends::jsonb, model = @model, prompt_fingerprint = @fingerprint,
            completed_at = now(), updated_at = now(), error_message = @error
        WHERE id = @id
        """;

    internal const string FailRun = """
        UPDATE skopos_coach_runs SET status = 'failed', error_message = left(@error, 1000),
            completed_at = now(), updated_at = now() WHERE id = @id
        """;
}
