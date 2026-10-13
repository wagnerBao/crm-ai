-- Operational state only; credentials and provider response bodies are never stored here.
create table if not exists ai_invocation_cooldowns (
    company_id uuid not null,
    agent_key text not null,
    configuration_fingerprint text not null,
    blocked_until timestamp with time zone not null,
    reason text not null,
    probe_id uuid,
    updated_at timestamp with time zone not null default now(),
    primary key (company_id, agent_key, configuration_fingerprint)
);
alter table ai_invocation_cooldowns add column if not exists probe_id uuid;
-- Upgrade an earlier bootstrap of this new operational table without removing its cooldowns.
do $$
begin
    if exists (
        select 1 from pg_constraint
        where conrelid = 'ai_invocation_cooldowns'::regclass and contype = 'p' and cardinality(conkey) = 2
    ) then
        alter table ai_invocation_cooldowns drop constraint ai_invocation_cooldowns_pkey;
        alter table ai_invocation_cooldowns
            add primary key (company_id, agent_key, configuration_fingerprint);
    end if;
end $$;
