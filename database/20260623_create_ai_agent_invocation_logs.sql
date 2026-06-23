create table if not exists ai_agent_invocation_logs (
    id uuid primary key,
    agent_key text not null,
    provider text not null,
    model text not null,
    operation text not null,
    platform_area text not null,
    endpoint text not null,
    http_status integer,
    success boolean not null,
    status text not null,
    request_json jsonb not null,
    response_json jsonb,
    result_json jsonb,
    error_type text,
    error_message text,
    prompt_tokens integer,
    completion_tokens integer,
    total_tokens integer,
    cached_prompt_tokens integer,
    reasoning_tokens integer,
    company_id uuid,
    opportunity_id uuid,
    whatsapp_conversation_id uuid,
    meeting_audio_recording_id uuid,
    activity_id uuid,
    account_id uuid,
    contact_id uuid,
    user_id uuid,
    context_entity_keys text[] not null default array[]::text[],
    metadata_json jsonb,
    started_at timestamptz not null,
    completed_at timestamptz not null,
    duration_ms integer not null,
    created_at timestamptz not null default now()
);

create index if not exists idx_ai_agent_invocation_logs_agent_started
    on ai_agent_invocation_logs (agent_key, started_at desc);

create index if not exists idx_ai_agent_invocation_logs_platform_started
    on ai_agent_invocation_logs (platform_area, started_at desc);

create index if not exists idx_ai_agent_invocation_logs_opportunity_started
    on ai_agent_invocation_logs (opportunity_id, started_at desc)
    where opportunity_id is not null;

create index if not exists idx_ai_agent_invocation_logs_whatsapp_conversation_started
    on ai_agent_invocation_logs (whatsapp_conversation_id, started_at desc)
    where whatsapp_conversation_id is not null;

create index if not exists idx_ai_agent_invocation_logs_success_started
    on ai_agent_invocation_logs (success, started_at desc);
