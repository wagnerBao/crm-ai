-- Read-only monitoring after publishing the context optimizations.
-- Character counts describe request size, not billed token savings.
-- Every API call is counted, including the second pass for ambiguous results.
-- Different context sizes and tenants can affect averages; these aggregates
-- do not establish savings or quality parity without comparable workloads.
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '30s';

SELECT company_id,
       agent_key,
       model,
       coalesce(metadata_json ->> 'contextOptimizationVersion', 'baseline') AS optimization_version,
       coalesce(metadata_json ->> 'contextMode', 'baseline') AS context_mode,
       count(*) AS api_calls_with_usage,
       count(*) FILTER (WHERE NOT success) AS unsuccessful_calls_with_usage,
       sum(prompt_tokens)::bigint AS input_tokens,
       sum(coalesce(completion_tokens, 0))::bigint AS output_tokens,
       sum(coalesce(cached_prompt_tokens, 0))::bigint AS cached_input_tokens,
       round(avg(prompt_tokens), 2) AS average_input_tokens,
       round(avg(completion_tokens), 2) AS average_output_tokens,
       round(100.0 * sum(coalesce(cached_prompt_tokens, 0)) / nullif(sum(prompt_tokens), 0), 2) AS cached_input_percent,
       round(avg((metadata_json ->> 'inputCharactersBefore')::numeric), 2) AS average_input_characters_before,
       round(avg((metadata_json ->> 'inputCharactersAfter')::numeric
                 + coalesce((metadata_json ->> 'addedInstructionCharacters')::numeric, 0)), 2) AS average_input_plus_extra_instruction_characters
FROM ai_agent_invocation_logs
WHERE started_at >= now() - interval '14 days'
  AND agent_key IN ('suggestion-completion-verification', 'daily-checkout', 'risk-analysis')
  AND prompt_tokens IS NOT NULL
GROUP BY company_id, agent_key, model, optimization_version, context_mode
ORDER BY company_id, agent_key, model, optimization_version, context_mode;

-- Monitor extra calls caused by ambiguous compact results, separately from
-- compact calls that failed before a parseable result was logged.
SELECT company_id,
       agent_key,
       metadata_json ->> 'contextOptimizationVersion' AS optimization_version,
       count(*) FILTER (WHERE metadata_json ->> 'contextMode' = 'compact' AND success) AS successful_compact_calls,
       count(*) FILTER (WHERE metadata_json ->> 'contextMode' = 'full-fallback') AS full_fallback_calls,
       count(*) FILTER (WHERE metadata_json ->> 'contextMode' = 'original') AS original_format_calls,
       sum(coalesce(prompt_tokens, 0) + coalesce(completion_tokens, 0))::bigint AS total_tokens_including_fallback
FROM ai_agent_invocation_logs
WHERE started_at >= now() - interval '14 days'
  AND metadata_json ->> 'contextOptimizationVersion' IS NOT NULL
GROUP BY company_id, agent_key, optimization_version
ORDER BY company_id, agent_key, optimization_version;

-- Paired risk validation. Agreement and score deltas are review signals,
-- not semantic quality parity. Shadow intentionally spends two API calls.
WITH pairs AS (
    SELECT company_id, opportunity_id, model,
           metadata_json ->> 'riskContextComparisonId' AS comparison_id,
           count(*) FILTER (WHERE metadata_json ->> 'contextMode' = 'shadow-full' AND success) AS full_successes,
           count(*) FILTER (WHERE metadata_json ->> 'contextMode' = 'shadow-compact' AND success) AS compact_successes,
           sum(prompt_tokens) FILTER (WHERE metadata_json ->> 'contextMode' = 'shadow-full') AS full_input_tokens,
           sum(prompt_tokens) FILTER (WHERE metadata_json ->> 'contextMode' = 'shadow-compact') AS compact_input_tokens,
           sum(coalesce(prompt_tokens, 0) + coalesce(completion_tokens, 0)) AS experiment_total_tokens,
           bool_or((metadata_json ->> 'riskLevelAgreement')::boolean) AS risk_level_agreement,
           max(abs((metadata_json ->> 'riskScoreDelta')::integer)) AS absolute_score_delta
    FROM ai_agent_invocation_logs
    WHERE started_at >= now() - interval '14 days'
      AND agent_key = 'risk-analysis'
      AND metadata_json ->> 'contextMode' IN ('shadow-full', 'shadow-compact')
      AND metadata_json ->> 'riskContextComparisonId' IS NOT NULL
    GROUP BY company_id, opportunity_id, model, comparison_id
)
SELECT company_id, model,
       count(*) AS comparison_attempts,
       count(*) FILTER (WHERE full_successes = 1 AND compact_successes = 1) AS completed_pairs,
       count(*) FILTER (WHERE risk_level_agreement = false) AS differing_risk_levels,
       round(avg(absolute_score_delta), 2) AS average_absolute_score_delta,
       round(avg(full_input_tokens) FILTER (WHERE full_successes = 1 AND compact_successes = 1), 2) AS paired_full_input_tokens,
       round(avg(compact_input_tokens) FILTER (WHERE full_successes = 1 AND compact_successes = 1), 2) AS paired_compact_input_tokens,
       sum(experiment_total_tokens)::bigint AS experiment_total_tokens
FROM pairs
GROUP BY company_id, model
ORDER BY company_id, model;

-- Verifier evidence selection: distinguish proposals from the records actually
-- sent, and include the cost of all baseline/selected/fallback calls.
SELECT company_id, model,
       metadata_json ->> 'evidenceSelectionVersion' AS selection_version,
       metadata_json ->> 'evidenceSelectionMode' AS selection_mode,
       metadata_json ->> 'evidenceSelectionRole' AS selection_role,
       count(*) AS api_calls,
       count(*) FILTER (WHERE NOT success) AS unsuccessful_calls,
       round(avg((metadata_json ->> 'evidencePoolCount')::numeric), 2) AS average_collected_records,
       round(avg((metadata_json ->> 'evidenceSentCount')::numeric), 2) AS average_sent_records,
       sum(coalesce(prompt_tokens, 0))::bigint AS input_tokens,
       sum(coalesce(completion_tokens, 0))::bigint AS output_tokens,
       sum(coalesce(prompt_tokens, 0) + coalesce(completion_tokens, 0))::bigint AS total_tokens
FROM ai_agent_invocation_logs
WHERE started_at >= now() - interval '14 days'
  AND agent_key = 'suggestion-completion-verification'
  AND metadata_json ->> 'evidenceSelectionVersion' IS NOT NULL
GROUP BY company_id, model, selection_version, selection_mode, selection_role
ORDER BY company_id, model, selection_version, selection_mode, selection_role;

-- Baseline may contain its original lossless-compaction fallback as well.
-- Status agreement alone does not establish correctness of attribution/proof.
WITH verifier_pairs AS (
    SELECT company_id, model,
           metadata_json ->> 'evidenceSelectionComparisonId' AS comparison_id,
           count(*) FILTER (WHERE metadata_json ->> 'evidenceSelectionRole' = 'baseline' AND success) AS baseline_successes,
           count(*) FILTER (WHERE metadata_json ->> 'evidenceSelectionRole' = 'selected' AND success) AS selected_successes,
           bool_or((metadata_json ->> 'verificationResultAgreement')::boolean) AS result_agreement,
           bool_or((metadata_json ->> 'selectedEvidenceResultSupported')::boolean) AS selected_result_supported,
           sum(prompt_tokens) FILTER (WHERE metadata_json ->> 'evidenceSelectionRole' = 'baseline') AS baseline_input_tokens,
           sum(prompt_tokens) FILTER (WHERE metadata_json ->> 'evidenceSelectionRole' = 'selected') AS selected_input_tokens,
           sum(coalesce(prompt_tokens, 0) + coalesce(completion_tokens, 0)) AS experiment_total_tokens
    FROM ai_agent_invocation_logs
    WHERE started_at >= now() - interval '14 days'
      AND agent_key = 'suggestion-completion-verification'
      AND metadata_json ->> 'evidenceSelectionMode' = 'shadow'
      AND metadata_json ->> 'evidenceSelectionComparisonId' IS NOT NULL
    GROUP BY company_id, model, comparison_id
)
SELECT company_id, model,
       count(*) AS comparison_attempts,
       count(*) FILTER (WHERE baseline_successes >= 1 AND selected_successes = 1) AS completed_pairs,
       count(*) FILTER (WHERE result_agreement = false) AS differing_results,
       count(*) FILTER (WHERE selected_result_supported = false) AS unsupported_subset_results,
       round(avg(baseline_input_tokens) FILTER (WHERE baseline_successes >= 1 AND selected_successes = 1), 2) AS paired_baseline_input_tokens_including_fallback,
       round(avg(selected_input_tokens) FILTER (WHERE baseline_successes >= 1 AND selected_successes = 1), 2) AS paired_selected_input_tokens,
       sum(experiment_total_tokens)::bigint AS experiment_total_tokens
FROM verifier_pairs
GROUP BY company_id, model
ORDER BY company_id, model;

ROLLBACK;
