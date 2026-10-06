-- Read-only production audit. Fixed windows use America/Sao_Paulo.
-- Snapshot cache queries reflect the state at execution, not a historical hit count.
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL TIME ZONE 'America/Sao_Paulo';

WITH windows(label,a,b) AS (VALUES
 ('before','2026-10-05 10:20:00-03'::timestamptz,'2026-10-05 13:19:00-03'::timestamptz),
 ('after','2026-10-06 10:20:00-03'::timestamptz,'2026-10-06 13:19:00-03'::timestamptz))
SELECT label,agent_key,count(prompt_tokens) metered_calls,
 count(*) FILTER(WHERE prompt_tokens IS NOT NULL AND coalesce(metadata_json->>'contextMode','baseline')<>'full-fallback') primary_calls,
 count(*) FILTER(WHERE prompt_tokens IS NOT NULL AND metadata_json->>'contextMode'='full-fallback') fallback_calls,
 sum(coalesce(prompt_tokens,0))::bigint input_tokens,sum(coalesce(completion_tokens,0))::bigint output_tokens,
 sum(coalesce(prompt_tokens,0)+coalesce(completion_tokens,0))::bigint total_tokens,
 round(avg(prompt_tokens),2) input_per_api_call,
 round(sum(coalesce(prompt_tokens,0)+coalesce(completion_tokens,0))::numeric / nullif(count(*) FILTER(WHERE prompt_tokens IS NOT NULL AND coalesce(metadata_json->>'contextMode','baseline')<>'full-fallback'),0),2) total_per_primary_execution,
 sum(coalesce(cached_prompt_tokens,0))::bigint cached_tokens
FROM windows w JOIN ai_agent_invocation_logs l ON started_at>=a AND started_at<b GROUP BY label,agent_key ORDER BY agent_key,label;

SELECT metadata_json->>'contextMode' mode,count(prompt_tokens) metered,
 sum(prompt_tokens)::bigint input_tokens,sum(completion_tokens)::bigint output_tokens,
 sum((metadata_json->>'inputCharactersBefore')::numeric) FILTER(WHERE prompt_tokens IS NOT NULL) chars_before,
 sum((metadata_json->>'inputCharactersAfter')::numeric+coalesce((metadata_json->>'addedInstructionCharacters')::numeric,0)) FILTER(WHERE prompt_tokens IS NOT NULL) chars_after
FROM ai_agent_invocation_logs WHERE agent_key='suggestion-completion-verification'
AND started_at>='2026-10-06 10:20:00-03' AND started_at<'2026-10-06 13:19:00-03'
GROUP BY mode ORDER BY mode;

-- Observed paired input tokens: second expanded call versus nearest compact call.
WITH pairs AS (
 SELECT f.company_id,f.prompt_tokens full_input,f.completion_tokens full_output,c.prompt_tokens compact_input,c.completion_tokens compact_output,
 f.started_at-c.started_at delay,c.id compact_id,f.id fallback_id,
 c.result_json->>'result' compact_result,f.result_json->>'result' full_result,
 (c.metadata_json->>'inputCharactersBefore')::int compact_original_chars,
 (f.metadata_json->>'inputCharactersBefore')::int full_original_chars
 FROM ai_agent_invocation_logs f JOIN LATERAL (
 SELECT c.* FROM ai_agent_invocation_logs c WHERE c.agent_key=f.agent_key AND c.company_id=f.company_id AND c.model=f.model
 AND c.metadata_json->>'suggestionId'=f.metadata_json->>'suggestionId'
 AND c.metadata_json->>'contextMode'='compact' AND c.prompt_tokens IS NOT NULL
 AND c.started_at<f.started_at AND c.started_at>f.started_at-interval '2 minutes'
 ORDER BY c.started_at DESC LIMIT 1) c ON TRUE
 WHERE f.agent_key='suggestion-completion-verification' AND f.metadata_json->>'contextMode'='full-fallback'
 AND f.prompt_tokens IS NOT NULL AND f.started_at>='2026-10-06 10:20:00-03' AND f.started_at<'2026-10-06 13:19:00-03'
)
SELECT count(*) pairs,count(DISTINCT compact_id) distinct_compact_calls,
 count(*) FILTER(WHERE compact_original_chars=full_original_chars) same_original_size,
 sum(full_input) full_input_tokens,sum(compact_input) compact_input_tokens,
 round(100.0*(sum(full_input)-sum(compact_input))/nullif(sum(full_input),0),2) paired_input_reduction_pct,
 sum(full_input+full_output+compact_input+compact_output) actual_tokens_both_passes,
 count(*) FILTER(WHERE compact_result IS DISTINCT FROM full_result) changed_results
FROM pairs;

WITH windows(label,a,b) AS (VALUES
 ('before','2026-10-05 10:20:00-03'::timestamptz,'2026-10-05 13:19:00-03'::timestamptz),
 ('after','2026-10-06 10:20:00-03'::timestamptz,'2026-10-06 13:19:00-03'::timestamptz))
SELECT label,result,count(*) verification_history_records FROM windows w JOIN ai_agent_suggestion_verifications v ON created_at>=a AND created_at<b GROUP BY label,result ORDER BY label,result;

-- A reused cache update does not insert a history row; current snapshot can identify examples, not a historical hit total.
SELECT verification_status,count(*) snapshot_reuse_candidates,
 count(*) FILTER(WHERE next_verification_at-last_verified_at BETWEEN interval '23 hours 59 minutes' AND interval '24 hours 1 minute') next_check_24h
FROM ai_agent_suggestions s WHERE s.last_verified_at>='2026-10-06 10:20:00-03' AND s.last_verified_at<'2026-10-06 13:19:00-03'
 AND s.verification_status IN ('unfulfilled','inconclusive')
 AND NOT EXISTS (SELECT 1 FROM ai_agent_suggestion_verifications v WHERE v.suggestion_id=s.id AND abs(extract(epoch FROM (v.created_at-s.last_verified_at)))<2)
GROUP BY verification_status;

SELECT agent_key,jsonb_typeof(request_json->'input') input_type,jsonb_typeof(result_json) result_type,count(*) n
FROM ai_agent_invocation_logs WHERE started_at>='2026-10-06 10:20:00-03' AND started_at<'2026-10-06 13:19:00-03' AND prompt_tokens IS NOT NULL
GROUP BY agent_key,input_type,result_type;
SELECT count(*) with_evidence_selection_metadata FROM ai_agent_invocation_logs
WHERE started_at>='2026-10-06 10:10:00-03' AND started_at<'2026-10-06 13:19:00-03' AND metadata_json ? 'evidenceSelectionVersion';


-- Current persisted snapshot: same cached fingerprint after last history event.
WITH candidate AS (
SELECT s.verification_status,s.evidence_fingerprint=v.evidence_fingerprint same_fingerprint,
 s.last_verified_at-v.created_at advancement,
 s.next_verification_at-s.last_verified_at next_delay
FROM ai_agent_suggestions s JOIN LATERAL (
 SELECT v.evidence_fingerprint,v.created_at FROM ai_agent_suggestion_verifications v
 WHERE v.suggestion_id=s.id ORDER BY v.created_at DESC LIMIT 1
) v ON true
WHERE s.last_verified_at>='2026-10-06 10:20:00-03' AND s.last_verified_at<'2026-10-06 13:19:00-03'
AND s.verification_status IN ('unfulfilled','inconclusive')
)
SELECT verification_status,count(*) FILTER(WHERE same_fingerprint AND advancement>interval '1 minute') unchanged_fingerprint_advanced_without_history,
 count(*) FILTER(WHERE same_fingerprint AND advancement>interval '1 minute' AND next_delay BETWEEN interval '14 minutes 59 seconds' AND interval '15 minutes 1 second') next_15m,
 count(*) FILTER(WHERE same_fingerprint AND advancement>interval '1 minute' AND next_delay BETWEEN interval '59 minutes 59 seconds' AND interval '60 minutes 1 second') next_60m,
 count(*) FILTER(WHERE same_fingerprint AND advancement>interval '1 minute' AND next_delay BETWEEN interval '23 hours 59 minutes' AND interval '24 hours 1 minute') next_24h
FROM candidate GROUP BY verification_status;
SELECT agent_key,response_json->'error'->>'code' provider_error_code,response_json->'error'->>'type' provider_error_type,count(*) errors
FROM ai_agent_invocation_logs WHERE started_at>='2026-10-06 10:20:00-03' AND started_at<'2026-10-06 13:19:00-03' AND http_status=429
GROUP BY agent_key,provider_error_code,provider_error_type ORDER BY agent_key;
SELECT agent_key,count(*) successes_with_missing_usage FROM ai_agent_invocation_logs
WHERE started_at>='2026-10-06 10:20:00-03' AND started_at<'2026-10-06 13:19:00-03' AND success AND prompt_tokens IS NULL GROUP BY agent_key;
SELECT current_setting('transaction_read_only') read_only,now() observed_at;

ROLLBACK;
