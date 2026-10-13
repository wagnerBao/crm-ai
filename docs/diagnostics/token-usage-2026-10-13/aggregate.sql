BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '60s';
SET LOCAL TIME ZONE 'America/Sao_Paulo';
SELECT json_build_object('section','audit','observed_at',now(),'cutoff','2026-10-13T11:00:00-03:00','read_only',current_setting('transaction_read_only'),'isolation',current_setting('transaction_isolation'));

-- Canonical measurement uses event time when present, otherwise invocation start.
-- A log joined to an event is never added a second time.
WITH canonical AS (
 SELECT e.created_at AS at,e.company_id,coalesce(l.agent_key,e.operation) AS agent_key,e.provider,e.model,e.operation,
 e.input_tokens,e.output_tokens,e.cached_input_tokens,e.calculated_credits,e.metering_mode,
 CASE WHEN l.id IS NULL THEN 'event-only' ELSE 'event+log' END AS source,
 coalesce(l.success,true) AS success,l.duration_ms,
 coalesce(-ledger.credits,0) AS charged_credits,
 coalesce(l.metadata_json->>'contextMode','baseline') AS context_mode
 FROM ai_usage_events e
 LEFT JOIN ai_agent_invocation_logs l ON l.id=CASE WHEN e.invocation_id ~ '^[0-9a-fA-F]{32}$' THEN e.invocation_id::uuid END AND l.company_id=e.company_id
 LEFT JOIN ai_credit_ledger_entries ledger ON ledger.company_id=e.company_id AND ledger.invocation_id=e.invocation_id AND ledger.entry_type='consume'
 WHERE e.created_at >= '2026-09-29 00:00-03' AND e.created_at < '2026-10-13 11:00-03'
 UNION ALL
 SELECT l.started_at,l.company_id,l.agent_key,l.provider,l.model,l.operation,
 coalesce(l.prompt_tokens,0),coalesce(l.completion_tokens,0),coalesce(l.cached_prompt_tokens,0),0::bigint,'unrecorded',
 'log-only',l.success,l.duration_ms,0::bigint,coalesce(l.metadata_json->>'contextMode','baseline')
 FROM ai_agent_invocation_logs l
 WHERE l.started_at >= '2026-09-29 00:00-03' AND l.started_at < '2026-10-13 11:00-03'
 AND (l.prompt_tokens IS NOT NULL OR l.completion_tokens IS NOT NULL)
 AND NOT EXISTS(SELECT 1 FROM ai_usage_events e WHERE e.company_id=l.company_id AND e.invocation_id=replace(l.id::text,'-',''))
), priced AS (
 SELECT x.*,CASE WHEN r.provider_input_usd_per_million IS NULL OR r.provider_output_usd_per_million IS NULL THEN NULL ELSE
 (greatest(0,x.input_tokens-x.cached_input_tokens)*r.provider_input_usd_per_million+x.output_tokens*r.provider_output_usd_per_million+x.cached_input_tokens*coalesce(r.provider_cached_input_usd_per_million,r.provider_input_usd_per_million))/1000000.0 END AS estimated_usd
 FROM canonical x LEFT JOIN LATERAL (
 SELECT r.* FROM ai_rate_cards r WHERE lower(r.provider)=lower(x.provider) AND (r.model_pattern='*' OR lower(r.model_pattern)=lower(x.model))
 AND r.effective_from<=x.at AND (r.effective_to IS NULL OR r.effective_to>x.at)
 ORDER BY CASE WHEN r.model_pattern='*' THEN 1 ELSE 0 END,r.effective_from DESC LIMIT 1
 ) r ON true
)
SELECT row_to_json(s) FROM (
 SELECT 'usage' AS section,at::date AS date_local,coalesce(c.name,'SEM EMPRESA') AS company,p.agent_key,provider,model,operation,metering_mode,source,context_mode,
 count(*) AS calls,count(*) FILTER(WHERE NOT success) AS failures_with_usage,
 sum(input_tokens)::bigint AS input_tokens,sum(output_tokens)::bigint AS output_tokens,sum(cached_input_tokens)::bigint AS cached_tokens,
 sum(input_tokens+output_tokens)::bigint AS total_tokens,sum(calculated_credits)::bigint AS calculated_credits,sum(charged_credits)::bigint AS charged_credits,
 coalesce(sum(estimated_usd),0) AS estimated_usd,count(*) FILTER(WHERE estimated_usd IS NULL) AS unpriced_calls,
 sum(input_tokens+output_tokens) FILTER(WHERE estimated_usd IS NULL)::bigint AS unpriced_tokens,
 round(avg(input_tokens+output_tokens),2) AS average_tokens,max(input_tokens+output_tokens) AS max_tokens,
 percentile_disc(0.95) WITHIN GROUP(ORDER BY input_tokens+output_tokens) AS p95_tokens,
 round(avg(duration_ms),2) AS mean_duration_ms,
 sum(input_tokens+output_tokens) FILTER(WHERE at::time<'11:00')::bigint AS tokens_before_11,
 sum(estimated_usd) FILTER(WHERE at::time<'11:00') AS usd_before_11
 FROM priced p LEFT JOIN companies c ON c.id=p.company_id
 GROUP BY date_local,company,p.agent_key,provider,model,operation,metering_mode,source,context_mode
 ORDER BY date_local,total_tokens DESC
) s;

SELECT row_to_json(s) FROM (
 SELECT 'attempts' AS section,started_at::date AS date_local,coalesce(c.name,'SEM EMPRESA') AS company,agent_key,model,
 count(*) AS attempts,count(*) FILTER(WHERE success) AS successes,
 count(*) FILTER(WHERE prompt_tokens IS NOT NULL OR completion_tokens IS NOT NULL) AS metered_calls,
 count(*) FILTER(WHERE success AND prompt_tokens IS NULL AND completion_tokens IS NULL) AS success_missing_usage,
 count(*) FILTER(WHERE NOT success) AS failures,
 count(*) FILTER(WHERE NOT success AND prompt_tokens IS NULL AND completion_tokens IS NULL) AS failures_without_usage,
 count(*) FILTER(WHERE NOT success AND (prompt_tokens IS NOT NULL OR completion_tokens IS NOT NULL)) AS failures_with_usage,
 sum(coalesce(prompt_tokens,0)+coalesce(completion_tokens,0))::bigint AS total_tokens,
 count(*) FILTER(WHERE coalesce(cached_prompt_tokens,0)>coalesce(prompt_tokens,0)) AS invalid_cache,
 count(*) FILTER(WHERE total_tokens IS NOT NULL AND total_tokens<>coalesce(prompt_tokens,0)+coalesce(completion_tokens,0)) AS inconsistent_total,
 percentile_disc(0.5) WITHIN GROUP(ORDER BY duration_ms) FILTER(WHERE success) AS p50_duration_ms,
 percentile_disc(0.95) WITHIN GROUP(ORDER BY duration_ms) FILTER(WHERE success) AS p95_duration_ms
 FROM ai_agent_invocation_logs l LEFT JOIN companies c ON c.id=l.company_id
 WHERE started_at >= '2026-09-29 00:00-03' AND started_at < '2026-10-13 11:00-03'
 GROUP BY date_local,company,agent_key,model ORDER BY date_local,attempts DESC
) s;

-- Classify errors without exporting bodies or customer messages.
SELECT row_to_json(s) FROM (
 SELECT 'errors' AS section,started_at::date AS date_local,coalesce(c.name,'SEM EMPRESA') AS company,agent_key,http_status,error_type,
 CASE WHEN error_message ILIKE '%API key was not configured%' THEN 'missing-local-key'
 WHEN error_message ILIKE '%AI_CREDITS_EXHAUSTED%' THEN 'credits-exhausted'
 ELSE coalesce(response_json->'error'->>'code','unclassified') END AS error_code,
 count(*) AS attempts,
 sum(coalesce(prompt_tokens,0)+coalesce(completion_tokens,0))::bigint AS registered_tokens
 FROM ai_agent_invocation_logs l LEFT JOIN companies c ON c.id=l.company_id
 WHERE started_at >= '2026-09-29 00:00-03' AND started_at < '2026-10-13 11:00-03' AND NOT success
 GROUP BY date_local,company,agent_key,http_status,error_type,error_code ORDER BY attempts DESC
) s;

SELECT row_to_json(s) FROM (
 SELECT 'settings' AS section,coalesce(c.name,'GLOBAL') AS company,s.agent_key,s.is_active,s.model,s.debounce_minutes,
 length(s.system_prompt) AS prompt_characters,length(coalesce(s.context_instructions,'')) AS context_characters,s.updated_at,
 nullif(btrim(s.api_key),'') IS NOT NULL AS has_own_key
 FROM ai_agent_settings s LEFT JOIN companies c ON c.id=s.company_id
 ORDER BY company,s.agent_key
) s;

SELECT row_to_json(s) FROM (
 SELECT 'pending' AS section,c.name AS company,s.verification_status,count(*) AS pending,
 count(*) FILTER(WHERE s.suggested_due_at IS NOT NULL) AS with_due_date,
 round(avg(s.verification_attempt_count),2) AS mean_lifetime_attempts,
 count(*) FILTER(WHERE s.evidence_fingerprint IS NOT NULL) AS with_fingerprint,
 count(*) FILTER(WHERE s.next_verification_at<='2026-10-13 11:00-03') AS due_at_cutoff,
 count(*) FILTER(WHERE s.created_at<'2026-10-06 11:00-03') AS over_7_days,
 count(*) FILTER(WHERE s.evidence_fingerprint LIKE 'v2:%') AS fingerprint_v2
 FROM ai_agent_suggestions s JOIN companies c ON c.id=s.company_id WHERE s.status='pending'
 GROUP BY company,s.verification_status ORDER BY pending DESC
) s;
ROLLBACK;
