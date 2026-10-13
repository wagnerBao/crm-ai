BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL TIME ZONE 'America/Sao_Paulo';
SELECT json_build_object('section','audit','observed_at',now(),'read_only',current_setting('transaction_read_only'),'isolation',current_setting('transaction_isolation'));
SELECT row_to_json(x) FROM (
 SELECT 'coverage' AS section,'events' AS source,count(*) AS records,min(created_at) AS earliest,max(created_at) AS latest
 FROM ai_usage_events WHERE created_at >= '2026-09-29 00:00-03'
 UNION ALL
 SELECT 'coverage','invocations',count(*),min(started_at),max(started_at)
 FROM ai_agent_invocation_logs WHERE started_at >= '2026-09-29 00:00-03'
) x;
SELECT row_to_json(x) FROM (SELECT 'columns' AS section,table_name,column_name,data_type
 FROM information_schema.columns WHERE table_name IN ('ai_rate_cards','ai_agent_suggestions','ai_agent_suggestion_verifications','ai_credit_ledger_entries') ORDER BY table_name,ordinal_position) x;
SELECT row_to_json(x) FROM (SELECT 'rates' AS section,provider,model_pattern,provider_input_usd_per_million,provider_output_usd_per_million,provider_cached_input_usd_per_million,provider_price_source_url,provider_price_verified_at,effective_from,effective_to
 FROM ai_rate_cards ORDER BY provider,model_pattern,effective_from) x;
ROLLBACK;
