BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL TIME ZONE 'America/Sao_Paulo';
SELECT json_build_object('section','audit','observed_at',now(),'cutoff',now(),'read_only',current_setting('transaction_read_only'),'isolation',current_setting('transaction_isolation'));

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
 WHERE e.created_at >= date_trunc('day',now()) AND e.created_at < now()
 UNION ALL
 SELECT l.started_at,l.company_id,l.agent_key,l.provider,l.model,l.operation,
 coalesce(l.prompt_tokens,0),coalesce(l.completion_tokens,0),coalesce(l.cached_prompt_tokens,0),0::bigint,'unrecorded',
 'log-only',l.success,l.duration_ms,0::bigint,coalesce(l.metadata_json->>'contextMode','baseline')
 FROM ai_agent_invocation_logs l
 WHERE l.started_at >= date_trunc('day',now()) AND l.started_at < now()
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
 WHERE started_at >= date_trunc('day',now()) AND started_at < now()
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
 WHERE started_at >= date_trunc('day',now()) AND started_at < now() AND NOT success
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
 count(*) FILTER(WHERE s.next_verification_at<=now()) AS due_at_cutoff,
 count(*) FILTER(WHERE s.created_at<now()-interval '7 days') AS over_7_days
 FROM ai_agent_suggestions s JOIN companies c ON c.id=s.company_id WHERE s.status='pending'
 GROUP BY company,s.verification_status ORDER BY pending DESC
) s;

SELECT row_to_json(s) FROM (
 SELECT 'last_hour_errors' AS section,coalesce(c.name,'SEM EMPRESA') AS company,l.agent_key,l.http_status,l.error_type,
 CASE WHEN error_message ILIKE '%API key was not configured%' THEN 'missing-local-key'
 WHEN error_message ILIKE '%AI_CREDITS_EXHAUSTED%' THEN 'credits-exhausted'
 ELSE coalesce(response_json->'error'->>'code','unclassified') END AS error_code,
 count(*) AS attempts,min(started_at) AS first_at,max(started_at) AS latest_at
 FROM ai_agent_invocation_logs l LEFT JOIN companies c ON c.id=l.company_id
 WHERE started_at>=now()-interval '1 hour' AND started_at<now() AND NOT success
 GROUP BY company,l.agent_key,l.http_status,l.error_type,error_code ORDER BY attempts DESC
) s;
SELECT row_to_json(s) FROM (
 SELECT 'latest_by_agent_company' AS section,coalesce(c.name,'SEM EMPRESA') AS company,l.agent_key,
 count(*) AS attempts_today,count(*) FILTER(WHERE success) AS successes_today,
 max(started_at) AS latest_attempt_at,max(started_at) FILTER(WHERE success) AS latest_success_at,
 max(started_at) FILTER(WHERE NOT success) AS latest_failure_at,
 count(*) FILTER(WHERE started_at>=now()-interval '1 hour') AS attempts_last_hour,
 count(*) FILTER(WHERE success AND started_at>=now()-interval '1 hour') AS successes_last_hour
 FROM ai_agent_invocation_logs l LEFT JOIN companies c ON c.id=l.company_id
 WHERE started_at>=date_trunc('day',now()) AND started_at<now()
 GROUP BY company,l.agent_key ORDER BY attempts_today DESC
) s;
WITH targets AS (
 SELECT DISTINCT company_id,agent_key FROM ai_agent_invocation_logs
 WHERE started_at>=date_trunc('day',now()) AND started_at<now()
), selected AS (
 SELECT t.company_id,t.agent_key,s.* FROM targets t LEFT JOIN LATERAL (
 SELECT settings.is_active,settings.provider,settings.model,settings.api_key,settings.company_id AS setting_company_id,settings.updated_at,settings.debounce_minutes
 FROM ai_agent_settings settings WHERE settings.agent_key=t.agent_key AND (settings.company_id=t.company_id OR settings.company_id IS NULL)
 ORDER BY CASE WHEN settings.company_id=t.company_id THEN 0 ELSE 1 END,settings.updated_at DESC LIMIT 1
 ) s ON true
)
SELECT row_to_json(s) FROM (
 SELECT 'credential_presence' AS section,coalesce(c.name,'SEM EMPRESA') AS company,x.agent_key,x.is_active,x.provider,x.model,x.debounce_minutes,x.updated_at,
 x.setting_company_id IS NULL AS global_settings_selected,
 x.is_active IS NULL AS settings_absent,
 nullif(btrim(x.api_key),'') IS NOT NULL AS has_selected_own_key,
 EXISTS(SELECT 1 FROM ai_agent_settings gs WHERE gs.agent_key=x.agent_key AND gs.company_id IS NULL AND nullif(btrim(gs.api_key),'') IS NOT NULL) AS has_global_same_agent_key,
 EXISTS(SELECT 1 FROM ai_agent_settings cs WHERE cs.agent_key=CASE x.agent_key WHEN 'call-audio-analysis' THEN 'meeting-service-analysis' WHEN 'skopos-individual-coach' THEN 'skopos-coach' WHEN 'suggestion-completion-verification' THEN 'whatsapp-conversation-analysis' ELSE NULL END
 AND (cs.company_id=x.company_id OR cs.company_id IS NULL) AND lower(btrim(cs.provider))=lower(btrim(x.provider)) AND nullif(btrim(cs.api_key),'') IS NOT NULL) AS has_supported_fallback_key
 FROM selected x LEFT JOIN companies c ON c.id=x.company_id ORDER BY company,x.agent_key
) s;
SELECT row_to_json(s) FROM (
 SELECT 'observed_policy_metadata' AS section,l.agent_key,
 l.metadata_json->>'contextOptimizationVersion' AS context_optimization_version,
 l.metadata_json->>'evidenceSelectionVersion' AS evidence_selection_version,
 l.metadata_json->>'contextMode' AS context_mode,
 count(*) AS calls,min(started_at) AS first_today_at,max(started_at) AS latest_today_at,
 sum(coalesce(prompt_tokens,0)+coalesce(completion_tokens,0)) AS tokens
 FROM ai_agent_invocation_logs l WHERE started_at>=date_trunc('day',now()) AND started_at<now() AND success
 GROUP BY l.agent_key,context_optimization_version,evidence_selection_version,context_mode ORDER BY calls DESC
) s;
SELECT row_to_json(s) FROM (
 SELECT 'version_metadata_keys' AS section,key,count(*) AS occurrences
 FROM ai_agent_invocation_logs l CROSS JOIN LATERAL jsonb_object_keys(l.metadata_json) key
 WHERE started_at>=date_trunc('day',now()) AND started_at<now() AND key ~* 'version|revision|sha|build|commit|deploy'
 GROUP BY key ORDER BY occurrences DESC
) s;
SELECT row_to_json(s) FROM (
 SELECT 'state_columns' AS section,table_name,column_name,data_type FROM information_schema.columns
 WHERE table_name IN ('risk_analysis_state','whatsapp_analysis_schedule','ai_agent_suggestion_verifications') ORDER BY table_name,ordinal_position
) s;
ROLLBACK;
