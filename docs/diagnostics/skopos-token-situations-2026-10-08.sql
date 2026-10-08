-- Read-only audit, fixed complete days in America/Sao_Paulo.
-- Each row is JSON. No messages, transcripts, titles or credentials are exported.
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL TIME ZONE 'America/Sao_Paulo';

SELECT row_to_json(s) FROM (
    SELECT 'agents' AS section,started_at::date AS date_local,agent_key,model,
           count(*) AS attempts,count(prompt_tokens) AS metered_calls,
           count(*) FILTER(WHERE NOT success) AS errors,
           sum(coalesce(prompt_tokens,0))::bigint AS input_tokens,
           sum(coalesce(completion_tokens,0))::bigint AS output_tokens,
           sum(coalesce(cached_prompt_tokens,0))::bigint AS cached_tokens,
           sum(coalesce(prompt_tokens,0)+coalesce(completion_tokens,0))::bigint AS total_tokens,
           round(avg(prompt_tokens+coalesce(completion_tokens,0)),2) AS mean_tokens_per_call,
           percentile_disc(0.95) WITHIN GROUP(ORDER BY prompt_tokens+coalesce(completion_tokens,0)) FILTER(WHERE prompt_tokens IS NOT NULL) AS p95_tokens,
           max(prompt_tokens+coalesce(completion_tokens,0)) AS max_tokens
    FROM ai_agent_invocation_logs
    WHERE started_at >= '2026-10-05 00:00-03' AND started_at < '2026-10-08 00:00-03'
    GROUP BY date_local,agent_key,model ORDER BY date_local,total_tokens DESC
) s;

SELECT row_to_json(s) FROM (
    SELECT 'companies' AS section,c.name AS company,l.agent_key,count(l.prompt_tokens) AS metered_calls,
           sum(coalesce(l.prompt_tokens,0)+coalesce(l.completion_tokens,0))::bigint AS total_tokens,
           round(avg(l.prompt_tokens+coalesce(l.completion_tokens,0)),2) AS mean_tokens_per_call,
           count(DISTINCT l.metadata_json->>'suggestionId') FILTER(WHERE l.prompt_tokens IS NOT NULL) AS suggestions
    FROM ai_agent_invocation_logs l JOIN companies c ON c.id=l.company_id
    WHERE l.started_at >= '2026-10-07 00:00-03' AND l.started_at < '2026-10-08 00:00-03'
      AND l.prompt_tokens IS NOT NULL
    GROUP BY company,l.agent_key ORDER BY total_tokens DESC
) s;

WITH calls AS (
    SELECT l.*,coalesce(l.metadata_json->>'contextMode','baseline') AS mode,
           CASE WHEN jsonb_typeof(l.request_json->'input')='string' THEN (l.request_json->>'input')::jsonb
                WHEN jsonb_typeof(l.request_json->'input')='object' THEN l.request_json->'input' END AS input
    FROM ai_agent_invocation_logs l
    WHERE l.agent_key='suggestion-completion-verification' AND l.prompt_tokens IS NOT NULL
      AND l.started_at >= '2026-10-07 00:00-03' AND l.started_at < '2026-10-08 00:00-03'
), sized AS (
    SELECT *,jsonb_array_length(input->'evidence') AS evidence_count,
           CASE WHEN jsonb_array_length(input->'evidence')<=10 THEN '01:1-10'
                WHEN jsonb_array_length(input->'evidence')<=40 THEN '02:11-40'
                WHEN jsonb_array_length(input->'evidence')<=80 THEN '03:41-80'
                WHEN jsonb_array_length(input->'evidence')<120 THEN '04:81-119'
                WHEN jsonb_array_length(input->'evidence')=120 THEN '05:120' ELSE 'other' END AS size_band
    FROM calls
)
SELECT row_to_json(s) FROM (
    SELECT 'evidence_size' AS section,size_band,mode,count(*) AS calls,
           sum(prompt_tokens+coalesce(completion_tokens,0))::bigint AS total_tokens,
           round(avg(prompt_tokens),2) AS mean_input_tokens,
           round(avg(completion_tokens),2) AS mean_output_tokens,
           round(avg(prompt_tokens+coalesce(completion_tokens,0)),2) AS mean_total_tokens,
           max(prompt_tokens+coalesce(completion_tokens,0)) AS max_total_tokens
    FROM sized GROUP BY size_band,mode ORDER BY size_band,mode
) s;

WITH calls AS (
    SELECT l.*,coalesce(metadata_json->>'contextMode','baseline') AS mode,
           (request_json->>'input')::jsonb AS input
    FROM ai_agent_invocation_logs l
    WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
), measured AS (
    SELECT l.*,CASE WHEN started_at-(input->>'createdAt')::timestamptz<interval '1 day' THEN '01:under-1-day'
                   WHEN started_at-(input->>'createdAt')::timestamptz<interval '7 days' THEN '02:1-7-days'
                   WHEN started_at-(input->>'createdAt')::timestamptz<interval '30 days' THEN '03:7-30-days'
                   ELSE '04:30-days-or-more' END AS age_band,
           input->>'dueAt' IS NOT NULL AS has_due_date
    FROM calls l
)
SELECT row_to_json(s) FROM (
    SELECT 'suggestion_age' AS section,age_band,has_due_date,count(*) AS calls,
           count(DISTINCT metadata_json->>'suggestionId') AS suggestions,
           sum(prompt_tokens+coalesce(completion_tokens,0))::bigint AS total_tokens,
           round(avg(prompt_tokens+coalesce(completion_tokens,0)),2) AS mean_tokens
    FROM measured GROUP BY age_band,has_due_date ORDER BY age_band,has_due_date
) s;

WITH ranked AS (
    SELECT l.*,coalesce(metadata_json->>'contextMode','baseline') AS mode,
           CASE WHEN metadata_json->>'contextMode' IN ('full-fallback','selection-full-fallback') THEN 'fallback'
                WHEN metadata_json->>'contextMode'='shadow-selected' THEN 'shadow'
                ELSE 'primary' END AS role,
           count(*) FILTER(WHERE coalesce(metadata_json->>'contextMode','baseline') NOT IN ('full-fallback','selection-full-fallback','shadow-selected'))
             OVER(PARTITION BY company_id,metadata_json->>'suggestionId' ORDER BY started_at,id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS primary_ordinal
    FROM ai_agent_invocation_logs l
    WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
), categorized AS (
    SELECT *,CASE WHEN role<>'primary' THEN role WHEN primary_ordinal=1 THEN 'first-primary-in-day' ELSE 'repeat-primary-in-day' END AS category
    FROM ranked
)
SELECT row_to_json(s) FROM (
    SELECT 'rechecks' AS section,c.name AS company,category,count(*) AS calls,
           count(DISTINCT metadata_json->>'suggestionId') AS suggestions,
           sum(prompt_tokens+coalesce(completion_tokens,0))::bigint AS total_tokens,
           round(avg(prompt_tokens+coalesce(completion_tokens,0)),2) AS mean_tokens
    FROM categorized l JOIN companies c ON c.id=l.company_id
    GROUP BY company,category ORDER BY company,category
) s;

SELECT row_to_json(s) FROM (
    SELECT 'verification_result' AS section,metadata_json->>'contextMode' AS mode,
           result_json->>'result' AS result,count(*) AS calls,
           sum(prompt_tokens+coalesce(completion_tokens,0))::bigint AS total_tokens,
           round(avg(prompt_tokens+coalesce(completion_tokens,0)),2) AS mean_tokens,
           round(avg((result_json->>'confidence')::numeric),2) AS mean_confidence
    FROM ai_agent_invocation_logs
    WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
    GROUP BY mode,result ORDER BY mode,result
) s;

-- Record-type character sizes are representation diagnostics, not token attribution.
WITH calls AS (
    SELECT coalesce(metadata_json->>'contextMode','baseline') AS mode,(request_json->>'input')::jsonb AS input
    FROM ai_agent_invocation_logs
    WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
)
SELECT row_to_json(s) FROM (
    SELECT 'evidence_types' AS section,mode,e->>'type' AS evidence_type,count(*) AS records,
           count(*) FILTER(WHERE (e->>'beforeSuggestion')::boolean) AS before_suggestion,
           sum(length(e::text))::bigint AS representation_characters,
           round(avg(length(e::text)),2) AS mean_record_characters
    FROM calls CROSS JOIN LATERAL jsonb_array_elements(input->'evidence') e
    GROUP BY mode,evidence_type ORDER BY mode,representation_characters DESC
) s;

-- Per-field request sizes for WhatsApp; do not export field contents.
WITH calls AS (
    SELECT id,(request_json->>'input')::jsonb AS input
    FROM ai_agent_invocation_logs
    WHERE agent_key='whatsapp-conversation-analysis' AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
)
SELECT row_to_json(s) FROM (
    SELECT 'whatsapp_fields' AS section,key AS field,count(*) AS appearances,
           sum(length(value::text))::bigint AS representation_characters,
           round(avg(length(value::text)),2) AS mean_characters
    FROM calls CROSS JOIN LATERAL jsonb_each(input)
    GROUP BY key ORDER BY representation_characters DESC
) s;

SELECT row_to_json(s) FROM (
    SELECT 'largest_suggestions' AS section,c.name AS company,
           l.metadata_json->>'suggestionId' AS suggestion_id,count(*) AS calls,
           count(*) FILTER(WHERE l.metadata_json->>'contextMode'='full-fallback') AS fallback_calls,
           sum(l.prompt_tokens+coalesce(l.completion_tokens,0))::bigint AS total_tokens,
           max(l.prompt_tokens+coalesce(l.completion_tokens,0)) AS largest_call_tokens
    FROM ai_agent_invocation_logs l JOIN companies c ON c.id=l.company_id
    WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
    GROUP BY company,l.metadata_json->>'suggestionId' ORDER BY total_tokens DESC LIMIT 10
) s;

WITH calls AS (
    SELECT coalesce(metadata_json->>'contextMode','baseline') AS mode,(request_json->>'input')::jsonb AS input
    FROM ai_agent_invocation_logs
    WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
), activities AS (
    SELECT mode,e,
           CASE WHEN jsonb_typeof(e->'summary')='string' THEN e->>'summary'
                WHEN e->'summary'->>'$ref' ~ '^#/evidence/[0-9]+/summary$'
                  THEN input->'evidence'->(split_part(e->'summary'->>'$ref','/',3)::integer)->>'summary'
           END AS summary
    FROM calls CROSS JOIN LATERAL jsonb_array_elements(input->'evidence') e
    WHERE e->>'type'='activity'
)
SELECT row_to_json(s) FROM (
    SELECT 'activity_kind' AS section,mode,
           CASE WHEN summary LIKE '% | agent-skopos | %' THEN 'agent-skopos'
                WHEN summary IS NULL THEN 'unknown' ELSE 'other-activity-types' END AS activity_kind,
           count(*) AS records,sum(length(e::text))::bigint AS representation_characters
    FROM activities GROUP BY mode,activity_kind ORDER BY mode,activity_kind
) s;

WITH calls AS (
    SELECT l.id,c.name AS company,
           l.request_json->>'input' AS input_text,l.request_json->>'instructions' AS instructions,
           (l.request_json->>'input')::jsonb AS input,
           l.prompt_tokens,l.completion_tokens
    FROM ai_agent_invocation_logs l JOIN companies c ON c.id=l.company_id
    WHERE agent_key='whatsapp-conversation-analysis' AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
)
SELECT row_to_json(s) FROM (
    SELECT 'whatsapp_company_payload' AS section,company,count(*) AS calls,
           round(avg(prompt_tokens),2) AS mean_input_tokens,round(avg(completion_tokens),2) AS mean_output_tokens,
           round(avg(length(input_text)),2) AS mean_input_characters,
           round(avg(length(instructions)),2) AS mean_instruction_characters,
           round(avg(coalesce(length((input->'scorecardTemplate')::text),0)),2) AS mean_scorecard_characters,
           round(avg(coalesce(length((input->'additionalContext')::text),0)),2) AS mean_additional_context_characters,
           round(avg(coalesce(length((input->'existingSuggestions')::text),0)),2) AS mean_existing_suggestion_characters,
           round(avg(coalesce(length((input->'newTranscript')::text),0)),2) AS mean_new_transcript_characters
    FROM calls GROUP BY company ORDER BY mean_input_tokens DESC
) s;

WITH calls AS (
    SELECT c.name AS company,l.request_json->>'instructions' AS instructions,
           (l.request_json->>'input')::jsonb->>'additionalContext' AS additional_context
    FROM ai_agent_invocation_logs l JOIN companies c ON c.id=l.company_id
    WHERE agent_key='whatsapp-conversation-analysis' AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
)
SELECT row_to_json(s) FROM (
    SELECT 'whatsapp_duplicate_context' AS section,company,count(*) AS calls,
           count(*) FILTER(WHERE nullif(additional_context,'') IS NOT NULL AND strpos(instructions,additional_context)>0) AS calls_with_exact_context_in_instructions,
           sum(length(additional_context)) FILTER(WHERE nullif(additional_context,'') IS NOT NULL AND strpos(instructions,additional_context)>0) AS repeated_context_characters
    FROM calls GROUP BY company ORDER BY company
) s;

SELECT json_build_object('section','audit','observed_at',now(),'read_only',current_setting('transaction_read_only'));
ROLLBACK;
