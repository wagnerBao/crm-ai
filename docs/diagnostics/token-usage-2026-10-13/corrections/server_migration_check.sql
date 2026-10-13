BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '15s';

SELECT jsonb_build_object(
    'section', 'session',
    'checked_at_brt', to_char(now() AT TIME ZONE 'America/Sao_Paulo', 'YYYY-MM-DD HH24:MI:SS'),
    'transaction_read_only', current_setting('transaction_read_only'),
    'resolved_cooldown_relation', to_regclass('ai_invocation_cooldowns')::text,
    'is_primary', NOT pg_is_in_recovery()
);

SELECT jsonb_build_object(
    'section', 'cooldown_tables',
    'tables', coalesce(jsonb_agg(jsonb_build_object(
        'schema', n.nspname,
        'name', c.relname,
        'kind', c.relkind,
        'schema_create_allowed', has_schema_privilege(n.oid, 'CREATE'),
        'table_owner_privileges', pg_has_role(c.relowner, 'USAGE'),
        'select_allowed', has_table_privilege(c.oid, 'SELECT'),
        'insert_allowed', has_table_privilege(c.oid, 'INSERT'),
        'update_allowed', has_table_privilege(c.oid, 'UPDATE'),
        'delete_allowed', has_table_privilege(c.oid, 'DELETE')
    ) ORDER BY n.nspname), '[]'::jsonb)
)
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relname = 'ai_invocation_cooldowns'
  AND n.nspname NOT IN ('pg_catalog', 'information_schema');

SELECT jsonb_build_object(
    'section', 'cooldown_columns',
    'columns', coalesce(jsonb_agg(jsonb_build_object(
        'name', a.attname,
        'type', format_type(a.atttypid, a.atttypmod),
        'not_null', a.attnotnull,
        'default', pg_get_expr(d.adbin, d.adrelid)
    ) ORDER BY a.attnum), '[]'::jsonb)
)
FROM pg_attribute a
LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
WHERE a.attrelid = to_regclass('ai_invocation_cooldowns')
  AND a.attnum > 0 AND NOT a.attisdropped;

SELECT jsonb_build_object(
    'section', 'cooldown_constraints',
    'constraints', coalesce(jsonb_agg(jsonb_build_object(
        'name', c.conname,
        'type', c.contype,
        'definition', pg_get_constraintdef(c.oid),
        'validated', c.convalidated
    ) ORDER BY c.conname), '[]'::jsonb)
)
FROM pg_constraint c
WHERE c.conrelid = to_regclass('ai_invocation_cooldowns');

SELECT jsonb_build_object(
    'section', 'deferred_status_columns',
    'columns', coalesce(jsonb_agg(jsonb_build_object(
        'table', c.relname,
        'column', a.attname,
        'type', format_type(a.atttypid, a.atttypmod),
        'checks', (SELECT coalesce(jsonb_agg(pg_get_constraintdef(k.oid)), '[]'::jsonb)
            FROM pg_constraint k
            WHERE k.conrelid = c.oid AND k.contype = 'c'
              AND a.attnum = ANY(k.conkey))
    ) ORDER BY c.relname, a.attname), '[]'::jsonb)
)
FROM pg_class c
JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
WHERE c.oid IN (
    to_regclass('whatsapp_conversation_analysis_runs'),
    to_regclass('whatsapp_conversations'),
    to_regclass('instagram_conversations')
)
  AND ((c.relname = 'whatsapp_conversation_analysis_runs' AND a.attname = 'status')
    OR (c.relname IN ('whatsapp_conversations', 'instagram_conversations') AND a.attname = 'last_analysis_status'));

ROLLBACK;
