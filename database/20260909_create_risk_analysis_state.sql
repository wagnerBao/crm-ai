-- Coordination metadata only. Payloads, delayed work and retries live in RabbitMQ.
create table if not exists risk_analysis_state (
    company_id uuid not null,
    opportunity_id uuid not null,
    revision bigint not null default 0,
    processed_revision bigint not null default 0,
    due_at timestamptz not null default now(),
    lease_id uuid,
    lease_until timestamptz,
    attempts integer not null default 0,
    last_error text,
    primary key(company_id, opportunity_id)
);
-- Compact idempotency receipts, no event JSON. No periodic polling of either table.
create table if not exists risk_analysis_event_receipts (
    company_id uuid not null,
    opportunity_id uuid not null,
    event_id text not null,
    revision bigint not null,
    primary key(company_id, opportunity_id, event_id),
    foreign key(company_id, opportunity_id) references risk_analysis_state(company_id, opportunity_id) on delete cascade
);
