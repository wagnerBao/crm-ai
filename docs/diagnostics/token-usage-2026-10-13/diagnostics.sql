BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '60s';
SET LOCAL TIME ZONE 'America/Sao_Paulo';
SELECT json_build_object('section','audit','observed_at',now(),'cutoff','2026-10-13T11:00:00-03:00','read_only',current_setting('transaction_read_only'));

WITH base AS (
 SELECT l.*,coalesce(metadata_json->>'contextMode','baseline') AS mode,
 CASE WHEN metadata_json->>'contextMode' IN ('full-fallback','selection-full-fallback') THEN 'fallback'
 WHEN metadata_json->>'contextMode'='shadow-selected' THEN 'shadow' ELSE 'primary' END AS role
 FROM ai_agent_invocation_logs l
 WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL
 AND started_at>='2026-09-29 00:00-03' AND started_at<'2026-10-13 11:00-03'
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
 AND started_at>='2026-09-29 00:00-03' AND started_at<'2026-10-13 11:00-03'
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
 AND started_at>='2026-09-29 00:00-03' AND started_at<'2026-10-13 11:00-03'
 GROUP BY date_local,mode,result ORDER BY date_local,total_tokens DESC
) s;

WITH identical AS (
 SELECT started_at::date AS date_local,company_id,model,coalesce(metadata_json->>'contextMode','baseline') AS mode,
 md5(coalesce(request_json->>'input','')) AS input_hash,md5(coalesce(request_json->>'instructions','')) AS instructions_hash,
 count(*) AS calls,sum(prompt_tokens+coalesce(completion_tokens,0))::bigint AS tokens
 FROM ai_agent_invocation_logs WHERE agent_key='suggestion-completion-verification' AND prompt_tokens IS NOT NULL AND success
 AND started_at>='2026-09-29 00:00-03' AND started_at<'2026-10-13 11:00-03'
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
 AND started_at>='2026-09-29 00:00-03' AND started_at<'2026-10-13 11:00-03'
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

-- Billing/coverage integrity; no ledger descriptions or customer text.
SELECT row_to_json(s) FROM (
 SELECT 'ledger' AS section,created_at::date AS date_local,entry_type,count(*) AS entries,sum(credits)::bigint AS credits
 FROM ai_credit_ledger_entries WHERE created_at>='2026-09-29 00:00-03' AND created_at<'2026-10-13 11:00-03'
 GROUP BY date_local,entry_type ORDER BY date_local,entry_type
) s;
SELECT row_to_json(s) FROM (
 SELECT 'cross_source' AS section,e.created_at::date AS date_local,count(*) AS matched,
 count(*) FILTER(WHERE e.input_tokens<>coalesce(l.prompt_tokens,0) OR e.output_tokens<>coalesce(l.completion_tokens,0) OR e.cached_input_tokens<>coalesce(l.cached_prompt_tokens,0)) AS usage_mismatches,
 count(*) FILTER(WHERE e.created_at::date<>l.started_at::date) AS different_day,
 count(*) FILTER(WHERE e.input_tokens+e.output_tokens=0) AS zero_usage_events,
 count(*) FILTER(WHERE e.created_at>='2026-10-13 11:00-03' AND l.started_at<'2026-10-13 11:00-03') AS crossed_cutoff
 FROM ai_usage_events e JOIN ai_agent_invocation_logs l ON l.id=CASE WHEN e.invocation_id ~ '^[0-9a-fA-F]{32}$' THEN e.invocation_id::uuid END AND l.company_id=e.company_id
 WHERE e.created_at>='2026-09-29 00:00-03' AND e.created_at<'2026-10-13 11:05-03'
 GROUP BY date_local ORDER BY date_local
) s;
ROLLBACK;
