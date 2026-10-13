BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout='60s';
SET LOCAL TIME ZONE 'America/Sao_Paulo';
SELECT json_build_object('section','audit','observed_at',now(),'cutoff','2026-10-13T11:00:00-03:00','read_only',current_setting('transaction_read_only'));

WITH calls AS (
 SELECT started_at::date AS date_local,coalesce(metadata_json->>'contextMode','baseline') AS mode,
 CASE WHEN jsonb_typeof(request_json->'input')='string' THEN (request_json->>'input')::jsonb
 WHEN jsonb_typeof(request_json->'input')='object' THEN request_json->'input' END AS input
 FROM ai_agent_invocation_logs WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
 AND started_at>='2026-10-07 00:00-03' AND started_at<'2026-10-13 11:00-03'
), evidence AS (
 SELECT date_local,mode,e,
 CASE WHEN jsonb_typeof(e->'summary')='string' THEN e->>'summary'
 WHEN e->'summary'->>'$ref' ~ '^#/evidence/[0-9]+/summary$'
 THEN input->'evidence'->(split_part(e->'summary'->>'$ref','/',3)::integer)->>'summary' END AS summary
 FROM calls CROSS JOIN LATERAL jsonb_array_elements(input->'evidence') e
)
SELECT row_to_json(s) FROM (
 SELECT 'evidence_types' AS section,date_local,mode,e->>'type' AS evidence_type,
 CASE WHEN e->>'type'='activity' AND summary LIKE '% | agent-skopos | %' THEN 'agent-skopos'
 WHEN e->>'type'='activity' THEN 'other-activity' ELSE 'other-evidence' END AS kind,
 count(*) AS records,sum(length(e::text))::bigint AS representation_chars
 FROM evidence GROUP BY date_local,mode,evidence_type,kind ORDER BY date_local,representation_chars DESC
) s;

WITH pairs AS (
 SELECT f.started_at::date AS date_local,f.company_id,f.prompt_tokens AS full_input,c.prompt_tokens AS compact_input,
 coalesce(f.completion_tokens,0) AS full_output,coalesce(c.completion_tokens,0) AS compact_output,
 c.result_json->>'result' AS compact_result,f.result_json->>'result' AS full_result
 FROM ai_agent_invocation_logs f JOIN LATERAL (
 SELECT c.* FROM ai_agent_invocation_logs c WHERE c.agent_key=f.agent_key AND c.company_id=f.company_id AND c.model=f.model
 AND c.metadata_json->>'suggestionId'=f.metadata_json->>'suggestionId'
 AND c.metadata_json->>'contextMode'='compact' AND c.prompt_tokens IS NOT NULL
 AND c.started_at<f.started_at AND c.started_at>f.started_at-interval '2 minutes'
 ORDER BY c.started_at DESC LIMIT 1
 ) c ON true
 WHERE f.agent_key='suggestion-completion-verification' AND f.metadata_json->>'contextMode'='full-fallback'
 AND f.prompt_tokens IS NOT NULL AND f.started_at>='2026-10-07 00:00-03' AND f.started_at<'2026-10-13 11:00-03'
)
SELECT row_to_json(s) FROM (
 SELECT 'fallback_pairs' AS section,date_local,c.name AS company,count(*) AS pairs,
 count(*) FILTER(WHERE compact_result IS DISTINCT FROM full_result) AS changed_results,
 sum(full_input+compact_input+full_output+compact_output)::bigint AS both_passes_tokens
 FROM pairs p LEFT JOIN companies c ON c.id=p.company_id GROUP BY date_local,company ORDER BY date_local,company
) s;

SELECT row_to_json(s) FROM (
 SELECT 'cross_source_exact' AS section,count(*) AS matched,
 count(*) FILTER(WHERE e.input_tokens<>coalesce(l.prompt_tokens,0) OR e.output_tokens<>coalesce(l.completion_tokens,0) OR e.cached_input_tokens<>coalesce(l.cached_prompt_tokens,0)) AS usage_mismatches,
 count(*) FILTER(WHERE e.created_at::date<>l.started_at::date) AS different_day
 FROM ai_usage_events e JOIN ai_agent_invocation_logs l ON l.id=CASE WHEN e.invocation_id ~ '^[0-9a-fA-F]{32}$' THEN e.invocation_id::uuid END AND l.company_id=e.company_id
 WHERE e.created_at>='2026-09-29 00:00-03' AND e.created_at<'2026-10-13 11:00-03'
) s;
SELECT json_build_object('section','audit_sessions','other_audit_connections',count(*)) FROM pg_stat_activity
 WHERE application_name='skopos-token-audit-readonly-2026-10-13' AND pid<>pg_backend_pid();
ROLLBACK;
