-- Production audit; aggregate data only. No credentials or business mutations.
-- Fixed windows use America/Sao_Paulo; costs use stored rate cards, not invoices.
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL TIME ZONE 'America/Sao_Paulo';

-- Summarize before pricing to avoid one rate-card lookup per invocation.
WITH usage AS (
    SELECT started_at::date AS date_local, company_id, agent_key, provider, model,
           count(*) AS attempts, count(prompt_tokens) AS metered_calls,
           count(*) FILTER (WHERE NOT success) AS errors,
           count(*) FILTER (WHERE prompt_tokens IS NOT NULL AND coalesce(metadata_json->>'contextMode','baseline') NOT IN ('full-fallback','selection-full-fallback','shadow-selected')) AS primary_calls,
           count(*) FILTER (WHERE prompt_tokens IS NOT NULL AND metadata_json->>'contextMode' IN ('full-fallback','selection-full-fallback')) AS fallback_calls,
           sum(coalesce(prompt_tokens,0))::bigint AS input_tokens,
           sum(coalesce(completion_tokens,0))::bigint AS output_tokens,
           sum(coalesce(cached_prompt_tokens,0))::bigint AS cached_tokens
    FROM ai_agent_invocation_logs
    WHERE started_at >= '2026-10-05 00:00-03' AND started_at < '2026-10-08 13:50-03'
    GROUP BY date_local,company_id,agent_key,provider,model
), priced AS (
    SELECT u.*, c.name AS company,
           ((input_tokens-cached_tokens)*r.provider_input_usd_per_million
            +output_tokens*r.provider_output_usd_per_million
            +cached_tokens*coalesce(r.provider_cached_input_usd_per_million,r.provider_input_usd_per_million))/1000000 AS estimated_usd
    FROM usage u LEFT JOIN companies c ON c.id=u.company_id
    LEFT JOIN LATERAL (
        SELECT r.* FROM ai_rate_cards r
        WHERE lower(r.provider)=lower(u.provider) AND (r.model_pattern='*' OR lower(r.model_pattern)=lower(u.model))
          AND r.effective_from <= u.date_local::timestamptz
          AND (r.effective_to IS NULL OR r.effective_to > (u.date_local+1)::timestamptz)
        ORDER BY CASE WHEN r.model_pattern='*' THEN 1 ELSE 0 END,r.effective_from DESC LIMIT 1
    ) r ON true
)
SELECT date_local,company,agent_key,model,attempts,metered_calls,errors,primary_calls,fallback_calls,
       input_tokens+output_tokens AS total_tokens,cached_tokens,round(estimated_usd,6) AS estimated_usd,
       round((input_tokens+output_tokens)::numeric/nullif(primary_calls,0),2) AS tokens_per_primary
FROM priced ORDER BY date_local,company,estimated_usd DESC NULLS LAST;

SELECT c.name AS company,l.agent_key,l.model,coalesce(l.metadata_json->>'contextMode','baseline') AS context_mode,
       count(prompt_tokens) AS metered_calls,sum(coalesce(prompt_tokens,0)+coalesce(completion_tokens,0))::bigint AS total_tokens,
       round(avg(prompt_tokens),2) AS mean_input,
       round(100.0*sum((metadata_json->>'inputCharactersBefore')::numeric-(metadata_json->>'inputCharactersAfter')::numeric-coalesce((metadata_json->>'addedInstructionCharacters')::numeric,0))
         /nullif(sum((metadata_json->>'inputCharactersBefore')::numeric),0),2) AS character_reduction_percent
FROM ai_agent_invocation_logs l JOIN companies c ON c.id=l.company_id
WHERE l.started_at >= '2026-10-07 00:00-03' AND l.started_at < '2026-10-08 00:00-03'
  AND l.agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
GROUP BY c.name,l.agent_key,l.model,context_mode ORDER BY company,context_mode;

-- No error bodies or customer messages are exported.
SELECT c.name AS company,l.agent_key,l.http_status,l.error_type,
       l.response_json->'error'->>'code' AS provider_code,count(*) AS errors
FROM ai_agent_invocation_logs l LEFT JOIN companies c ON c.id=l.company_id
WHERE l.started_at >= '2026-10-07 00:00-03' AND l.started_at < '2026-10-08 13:50-03' AND NOT l.success
GROUP BY c.name,l.agent_key,l.http_status,l.error_type,provider_code ORDER BY errors DESC;

SELECT c.name AS company,s.verification_status,count(*) AS pending,
       count(*) FILTER(WHERE s.suggested_due_at IS NOT NULL) AS with_due_date,
       round(avg(s.verification_attempt_count),2) AS average_attempt_count,
       count(*) FILTER(WHERE s.evidence_fingerprint IS NOT NULL) AS with_fingerprint,
       count(*) FILTER(WHERE s.next_verification_at<=now()) AS currently_due
FROM ai_agent_suggestions s JOIN companies c ON c.id=s.company_id
WHERE s.status='pending' GROUP BY company,s.verification_status ORDER BY company,s.verification_status;

SELECT c.name AS company,v.result,count(*) AS verifications,count(DISTINCT v.suggestion_id) AS suggestions,
       count(*) FILTER(WHERE v.reason='Nenhum registro posterior à sugestão foi encontrado.') AS deterministic_no_evidence,
       count(DISTINCT v.evidence_fingerprint) AS fingerprints
FROM ai_agent_suggestion_verifications v JOIN companies c ON c.id=v.company_id
WHERE v.created_at >= '2026-10-07 00:00-03' AND v.created_at < '2026-10-08 00:00-03'
GROUP BY company,v.result ORDER BY company,v.result;

SELECT coalesce(c.name,'GLOBAL') AS company,s.agent_key,s.is_active,s.model,s.debounce_minutes,
       s.context_entity_keys,length(s.system_prompt) AS prompt_characters,
       length(coalesce(s.context_instructions,'')) AS extra_instruction_characters,s.updated_at,
       nullif(btrim(s.api_key),'') IS NOT NULL AS has_own_key
FROM ai_agent_settings s LEFT JOIN companies c ON c.id=s.company_id
WHERE s.agent_key IN ('suggestion-completion-verification','whatsapp-conversation-analysis','instagram-conversation-analysis','risk-analysis','daily-checkout')
ORDER BY company,s.agent_key;

SELECT model_pattern,provider_input_usd_per_million,provider_output_usd_per_million,
       provider_cached_input_usd_per_million,provider_price_verified_at,effective_from,effective_to
FROM ai_rate_cards ORDER BY model_pattern;
-- Duplicated identical requests in one local day (no customer content exported).
WITH identical AS (
    SELECT company_id,model,coalesce(metadata_json->>'contextMode','baseline') AS mode,
           md5(coalesce(request_json->>'input','')),md5(coalesce(request_json->>'instructions','')),
           count(*) AS calls,sum(coalesce(prompt_tokens,0)+coalesce(completion_tokens,0))::bigint AS tokens
    FROM ai_agent_invocation_logs
    WHERE agent_key='suggestion-completion-verification' AND success AND prompt_tokens IS NOT NULL
      AND started_at >= '2026-10-07 00:00-03' AND started_at < '2026-10-08 00:00-03'
    GROUP BY company_id,model,mode,md5(coalesce(request_json->>'input','')),md5(coalesce(request_json->>'instructions',''))
)
SELECT c.name AS company,sum(calls) AS calls,count(*) AS distinct_requests,
       sum(calls-1) AS repeated_identical_calls,sum(tokens) FILTER(WHERE calls>1) AS tokens_in_repeated_groups
FROM identical i JOIN companies c ON c.id=i.company_id GROUP BY company ORDER BY calls DESC;

-- Metering has its own event time and coverage; do not confuse calculated credits with provider USD.
SELECT c.name AS company,e.operation,e.model,e.metering_mode,count(*) AS metered_events,
       sum(e.calculated_credits) AS calculated_credits,sum(e.input_tokens+e.output_tokens) AS tokens,
       sum(e.cached_input_tokens) AS cached_tokens
FROM ai_usage_events e JOIN companies c ON c.id=e.company_id
WHERE e.created_at >= '2026-10-07 00:00-03' AND e.created_at < '2026-10-08 00:00-03'
GROUP BY company,e.operation,e.model,e.metering_mode ORDER BY company,calculated_credits DESC;

SELECT c.name AS company,l.agent_key,count(*) AS missing_key_failures
FROM ai_agent_invocation_logs l JOIN companies c ON c.id=l.company_id
WHERE l.started_at >= '2026-10-07 00:00-03' AND l.started_at < '2026-10-08 13:50-03'
  AND NOT l.success AND l.http_status IS NULL AND l.error_message ILIKE '%API key was not configured%'
GROUP BY company,l.agent_key ORDER BY missing_key_failures DESC;

-- Expose only result status, pair size, and saving distributions, never evidence text.
WITH pairs AS (
    SELECT f.company_id,f.prompt_tokens AS full_input,c.prompt_tokens AS compact_input,
           f.completion_tokens AS full_output,c.completion_tokens AS compact_output,
           c.result_json->>'result' AS compact_result,f.result_json->>'result' AS full_result
    FROM ai_agent_invocation_logs f JOIN LATERAL (
        SELECT c.* FROM ai_agent_invocation_logs c
        WHERE c.agent_key=f.agent_key AND c.company_id=f.company_id AND c.model=f.model
          AND c.metadata_json->>'suggestionId'=f.metadata_json->>'suggestionId'
          AND c.metadata_json->>'contextMode'='compact' AND c.prompt_tokens IS NOT NULL
          AND c.started_at<f.started_at AND c.started_at>f.started_at-interval '2 minutes'
        ORDER BY c.started_at DESC LIMIT 1
    ) c ON true
    WHERE f.agent_key='suggestion-completion-verification' AND f.metadata_json->>'contextMode'='full-fallback'
      AND f.prompt_tokens IS NOT NULL AND f.started_at >= '2026-10-07 00:00-03' AND f.started_at < '2026-10-08 00:00-03'
)
SELECT c.name AS company,count(*) AS pairs,sum(full_input) AS full_input,sum(compact_input) AS compact_input,
       count(*) FILTER(WHERE compact_result IS DISTINCT FROM full_result) AS changed_results,
       sum(full_input+compact_input+full_output+compact_output) AS tokens_in_both_passes
FROM pairs p JOIN companies c ON c.id=p.company_id GROUP BY company ORDER BY pairs DESC;

WITH primary_calls AS (
    SELECT l.company_id,l.metadata_json->>'suggestionId' AS suggestion_id,l.started_at,
           l.started_at-lag(l.started_at) OVER (
               PARTITION BY l.company_id,l.metadata_json->>'suggestionId' ORDER BY l.started_at
           ) AS previous_gap
    FROM ai_agent_invocation_logs l
    WHERE l.agent_key='suggestion-completion-verification' AND l.prompt_tokens IS NOT NULL
      AND coalesce(l.metadata_json->>'contextMode','baseline') NOT IN ('full-fallback','selection-full-fallback','shadow-selected')
      AND l.started_at >= '2026-10-07 00:00-03' AND l.started_at < '2026-10-08 00:00-03'
)
SELECT c.name AS company,count(*) AS primary_calls,count(DISTINCT suggestion_id) AS suggestions,
       count(*) FILTER(WHERE previous_gap<interval '5 minutes') AS gap_under_5_minutes,
       count(*) FILTER(WHERE previous_gap<interval '15 minutes') AS gap_under_15_minutes
FROM primary_calls p JOIN companies c ON c.id=p.company_id GROUP BY company ORDER BY primary_calls DESC;

SELECT now() AS observed_at,current_setting('transaction_read_only') AS read_only;
ROLLBACK;
