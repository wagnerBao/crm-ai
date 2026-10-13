BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL TIME ZONE 'America/Sao_Paulo';
SELECT json_build_object('section','audit','observed_at',now(),'cutoff','2026-10-13 12:07:36.015118-03'::timestamptz,'read_only',current_setting('transaction_read_only'));

WITH base AS (
 SELECT l.*,coalesce(metadata_json->>'contextMode','baseline') AS mode,
 CASE WHEN metadata_json->>'contextMode' IN ('full-fallback','selection-full-fallback') THEN 'fallback'
 WHEN metadata_json->>'contextMode'='shadow-selected' THEN 'shadow' ELSE 'primary' END AS role
 FROM ai_agent_invocation_logs l
 WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
 AND started_at>='2026-10-13 00:00-03' AND started_at<'2026-10-13 12:07:36.015118-03'::timestamptz
), ranked AS (
 SELECT *,count(*) FILTER(WHERE role='primary') OVER(PARTITION BY company_id,metadata_json->>'suggestionId',started_at::date ORDER BY started_at,id) AS primary_ordinal,
 started_at-lag(started_at) OVER(PARTITION BY company_id,metadata_json->>'suggestionId',role ORDER BY started_at,id) AS previous_gap
 FROM base
)
SELECT row_to_json(s) FROM (
 SELECT 'rechecks' AS section,started_at::date AS date_local,c.name AS company,
 CASE WHEN metadata_json->>'suggestionId' IS NULL THEN 'missing-suggestion-id' WHEN role<>'primary' THEN role
 WHEN primary_ordinal=1 THEN 'first-primary-in-day' ELSE 'repeat-primary-in-day' END AS category,
 count(*) AS calls,count(DISTINCT metadata_json->>'suggestionId') AS suggestions,
 sum(prompt_tokens+coalesce(completion_tokens,0))::bigint AS total_tokens,
 count(*) FILTER(WHERE role='primary' AND previous_gap<interval '5 minutes') AS gap_under_5m,
 count(*) FILTER(WHERE role='primary' AND previous_gap<interval '15 minutes') AS gap_under_15m
 FROM ranked l LEFT JOIN companies c ON c.id=l.company_id
 GROUP BY date_local,company,category ORDER BY date_local,total_tokens DESC
) s;

WITH calls AS (
 SELECT l.*,coalesce(metadata_json->>'contextMode','baseline') AS mode,
 CASE WHEN jsonb_typeof(request_json->'input')='string' THEN (request_json->>'input')::jsonb
 WHEN jsonb_typeof(request_json->'input')='object' THEN request_json->'input' END AS input
 FROM ai_agent_invocation_logs l WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
 AND started_at>='2026-10-13 00:00-03' AND started_at<'2026-10-13 12:07:36.015118-03'::timestamptz
)
SELECT row_to_json(s) FROM (
 SELECT 'evidence_size' AS section,started_at::date AS date_local,mode,
 CASE WHEN jsonb_array_length(input->'evidence')<=10 THEN '01:0-10' WHEN jsonb_array_length(input->'evidence')<=40 THEN '02:11-40'
 WHEN jsonb_array_length(input->'evidence')<=80 THEN '03:41-80' WHEN jsonb_array_length(input->'evidence')<120 THEN '04:81-119'
 WHEN jsonb_array_length(input->'evidence')=120 THEN '05:120' ELSE 'other' END AS size_band,
 count(*) AS calls,sum(prompt_tokens+coalesce(completion_tokens,0))::bigint AS total_tokens,
 sum(coalesce((metadata_json->>'inputCharactersBefore')::numeric,0)) AS chars_before,
 sum(coalesce((metadata_json->>'inputCharactersAfter')::numeric,0)) AS chars_after,
 sum(coalesce((metadata_json->>'addedInstructionCharacters')::numeric,0)) AS added_instruction_chars
 FROM calls GROUP BY date_local,mode,size_band ORDER BY date_local,size_band,mode
) s;

SELECT row_to_json(s) FROM (
 SELECT 'verification_result' AS section,started_at::date AS date_local,coalesce(metadata_json->>'contextMode','baseline') AS mode,
 result_json->>'result' AS result,count(*) AS calls,sum(prompt_tokens+coalesce(completion_tokens,0))::bigint AS total_tokens
 FROM ai_agent_invocation_logs WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
 AND started_at>='2026-10-13 00:00-03' AND started_at<'2026-10-13 12:07:36.015118-03'::timestamptz
 GROUP BY date_local,mode,result ORDER BY date_local,total_tokens DESC
) s;

WITH identical AS (
 SELECT started_at::date AS date_local,company_id,model,coalesce(metadata_json->>'contextMode','baseline') AS mode,
 md5(coalesce(request_json->>'input','')) AS input_hash,md5(coalesce(request_json->>'instructions','')) AS instructions_hash,
 count(*) AS calls,sum(prompt_tokens+coalesce(completion_tokens,0))::bigint AS tokens
 FROM ai_agent_invocation_logs WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL AND success
 AND started_at>='2026-10-13 00:00-03' AND started_at<'2026-10-13 12:07:36.015118-03'::timestamptz
 GROUP BY date_local,company_id,model,mode,input_hash,instructions_hash
)
SELECT row_to_json(s) FROM (
 SELECT 'identical_requests' AS section,date_local,c.name AS company,sum(calls)::bigint AS calls,count(*) AS distinct_requests,
 sum(calls-1)::bigint AS repeated_identical_calls,coalesce(sum(tokens) FILTER(WHERE calls>1),0)::bigint AS tokens_in_repeated_groups
 FROM identical i LEFT JOIN companies c ON c.id=i.company_id GROUP BY date_local,company ORDER BY date_local,company
) s;

WITH calls AS (
 SELECT l.started_at::date AS date_local,c.name AS company,l.request_json->>'instructions' AS instructions,
 CASE WHEN jsonb_typeof(request_json->'input')='string' THEN (request_json->>'input')::jsonb
 WHEN jsonb_typeof(request_json->'input')='object' THEN request_json->'input' END AS input
 FROM ai_agent_invocation_logs l LEFT JOIN companies c ON c.id=l.company_id
 WHERE agent_key='whatsapp-conversation-analysis' AND prompt_tokens IS NOT NULL
 AND started_at>='2026-10-13 00:00-03' AND started_at<'2026-10-13 12:07:36.015118-03'::timestamptz
)
SELECT row_to_json(s) FROM (
 SELECT 'whatsapp_payload' AS section,date_local,company,count(*) AS calls,
 count(*) FILTER(WHERE nullif(input->>'additionalContext','') IS NOT NULL AND strpos(instructions,input->>'additionalContext')>0) AS duplicate_calls,
 sum(length(input->>'additionalContext')) FILTER(WHERE nullif(input->>'additionalContext','') IS NOT NULL AND strpos(instructions,input->>'additionalContext')>0) AS repeated_chars,
 sum(length(coalesce(instructions,''))) AS instructions_chars,
 sum(length(input::text)) AS input_chars,
 sum(coalesce(length((input->'scorecardTemplate')::text),0)+coalesce(length((input->'scorecardState')::text),0)) AS scorecard_chars,
 sum(coalesce(length((input->'additionalContext')::text),0)) AS additional_context_chars,
 sum(coalesce(length((input->'existingSuggestions')::text),0)) AS existing_suggestions_chars,
 sum(coalesce(length((input->'newTranscript')::text),0)) AS new_transcript_chars
 FROM calls GROUP BY date_local,company ORDER BY date_local,company
) s;


SELECT row_to_json(s) FROM (
 SELECT 'verification_outcomes_today' AS section,c.name AS company,v.result,count(*) AS outcomes,
 count(*) FILTER(WHERE v.evidence_fingerprint IS NULL) AS no_fingerprint,
 max(v.created_at) AS latest_at
 FROM ai_agent_suggestion_verifications v JOIN companies c ON c.id=v.company_id WHERE v.created_at>='2026-10-13 00:00-03' AND v.created_at<'2026-10-13 12:07:36.015118-03'::timestamptz
 GROUP BY c.name,v.result ORDER BY outcomes DESC
) s;
SELECT row_to_json(s) FROM (
 SELECT 'risk_work_state' AS section,c.name AS company,count(*) AS tracked,
 count(*) FILTER(WHERE revision>processed_revision) AS unprocessed,
 count(*) FILTER(WHERE revision>processed_revision AND due_at<=now()) AS due,
 count(*) FILTER(WHERE revision>processed_revision AND lease_until>now()) AS leased,
 max(attempts) AS max_attempts
 FROM risk_analysis_state r JOIN companies c ON c.id=r.company_id GROUP BY c.name ORDER BY unprocessed DESC
) s;
ROLLBACK;
