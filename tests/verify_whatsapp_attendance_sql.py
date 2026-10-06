"""Emit SQL regression checks using the actual worker/store queries.

Usage: python3 tests/verify_whatsapp_attendance_sql.py --consumers-root /path/to/CONSUMERS_CRM | psql ...
All writes use temporary tables and the transaction is rolled back. No credentials are stored.
Run against a database with the current CRM schema (for the suggestion table definition).
"""
import argparse
import re
import textwrap
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--consumers-root", type=Path, required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
worker = (args.consumers_root / "src/CrmRestApi.Consumers.Worker/Consumers/WhatsappConversationAnalysisConsumer.cs").read_text()
store = (root / "src/CrmAi.Infrastructure/Persistence/PostgresWhatsappConversationActionStore.cs").read_text()


def query(source, method):
    declaration = re.search(r"(?:private|public)[^\n]*\b" + re.escape(method) + r"\(", source)
    body = source[declaration.start():]
    return textwrap.dedent(re.search(r'"""\n(.*?)\n\s*"""', body, re.S).group(1))


def uid(number):
    return f"'00000000-0000-0000-0000-{number:012x}'::uuid"


def bind(sql, values):
    return re.sub(r"@(\w+)", lambda match: values[match.group(1)], sql)


def check(condition, label):
    print(f"do $$ begin if not ({condition}) then raise exception '{label}'; end if; end $$;")
    print(f"select 'PASS: {label}';")


print("""begin;
set local statement_timeout = '30s';
set local lock_timeout = '5s';
set local search_path = pg_temp, public;
create temporary table whatsapp_conversations (
    id uuid primary key, last_analysis_status text, last_analysis_at timestamptz,
    last_analyzed_message_at timestamptz, last_message_at timestamptz, updated_at timestamptz default now());
create temporary table whatsapp_conversation_analysis_runs (
    id uuid primary key, conversation_id uuid, last_message_id uuid,
    window_end_at timestamptz, status text, error text, updated_at timestamptz default now());
create temporary table whatsapp_messages (
    id uuid primary key default gen_random_uuid(), conversation_id uuid, direction text,
    status text, message_at timestamptz, created_at timestamptz default now());
create temporary table ai_agent_suggestions (like public.ai_agent_suggestions including all);
""")
queued = bind(query(worker, "MarkRunQueuedAsync"), {"RunId": uid(101), "ConversationId": uid(3)})
for status in ("completed", "failed", "processing"):
    print("truncate whatsapp_conversations, whatsapp_conversation_analysis_runs;")
    print(f"insert into whatsapp_conversations(id,last_analysis_status) values({uid(3)},'{status}');")
    print(f"insert into whatsapp_conversation_analysis_runs(id,conversation_id,status) values({uid(101)},{uid(3)},'{status}');")
    print(queued)
    expected = "queued" if status == "processing" else status
    check(f"(select last_analysis_status='{expected}' from whatsapp_conversations) and (select status='{expected}' from whatsapp_conversation_analysis_runs)", f"dispatcher preserves {status} state")

# Reproduce completion between the dispatcher's two UPDATE statements.
print("truncate whatsapp_conversations, whatsapp_conversation_analysis_runs;")
print(f"insert into whatsapp_conversations(id,last_analysis_status) values({uid(3)},'processing');")
print(f"insert into whatsapp_conversation_analysis_runs(id,conversation_id,status) values({uid(101)},{uid(3)},'processing');")
run_update, conversation_update = queued.split("update whatsapp_conversations", 1)
print(run_update)
print("update whatsapp_conversation_analysis_runs set status='completed'; update whatsapp_conversations set last_analysis_status='completed';")
print("update whatsapp_conversations" + conversation_update)
check("(select last_analysis_status='completed' from whatsapp_conversations)", "completion between dispatcher updates cannot leave conversation queued")

print("truncate whatsapp_conversations, whatsapp_conversation_analysis_runs;")
for n, status, minutes in ((3, "queued", 30), (4, "processing", 30), (5, "processing", 1), (6, "queued", 30), (7, "queued", 30), (8, "queued", 30)):
    print(f"insert into whatsapp_conversations(id,last_analysis_status,last_analysis_at,last_message_at) values({uid(n)},'{status}',now()-interval '{minutes} minutes',now()-interval '1 hour');")
print(f"insert into whatsapp_conversation_analysis_runs(id,conversation_id,status,window_end_at,updated_at) values({uid(103)},{uid(3)},'completed',now()-interval '1 hour',now()-interval '30 minutes'),({uid(106)},{uid(6)},'processing',now()-interval '1 hour',now()),({uid(107)},{uid(7)},'queued',now()-interval '1 hour',now()-interval '30 minutes'),({uid(108)},{uid(8)},'completed',now()-interval '2 hours',now()-interval '30 minutes');")
print(bind(query(worker, "RecoverStaleRunsAsync"), {"RetryMinutes": "15"}))
for n, expected, label in ((3, "completed", "completed orphan restores checkpoint"), (4, "failed", "claim without run is recovered"), (5, "processing", "fresh claim is preserved"), (6, "queued", "active run is preserved"), (7, "failed", "expired run is recovered"), (8, "failed", "orphan with new messages becomes eligible")):
    check(f"(select last_analysis_status='{expected}' from whatsapp_conversations where id={uid(n)})", label)
check(f"(select last_analyzed_message_at=last_message_at from whatsapp_conversations where id={uid(3)})", "recovered checkpoint retains completed window")

suggestion_sql = query(store, "InsertAgentSuggestionAsync")
parameters = {
    "id": uid(20), "companyId": uid(1), "contactId": uid(2), "conversationId": uid(3),
    "runId": uid(200), "type": "'activity'", "title": "'Responder nova mensagem'", "description": "'Continuar cotacao'",
    "dueAt": "null::timestamptz", "payload": "'{\"semanticIntentKey\":\"cotacao\"}'::jsonb",
    "matchingSuggestionId": uid(10), "semanticIntentKey": "'cotacao'", "responseRequiredAt": "now()-interval '1 day'",
    "generationModel": "'test'", "promptFingerprint": "'test'", "confidenceScore": "90", "generationReasons": "'[]'::jsonb",
}
for status, incoming, deleted, expected in (
    ("rejected", True, False, "pending"), ("rejected", False, False, "rejected"),
    ("rejected", True, True, "rejected"), ("pending", True, False, "pending"),
    ("accepted", False, False, "accepted"), ("accepted", True, False, "accepted"),
):
    print("truncate ai_agent_suggestions, whatsapp_messages;")
    print(f"insert into ai_agent_suggestions(id,company_id,contact_id,conversation_id,run_id,agent_key,suggestion_type,status,title,description,payload,resolved_at) values({uid(10)},{uid(1)},{uid(2)},{uid(3)},{uid(100)},'whatsapp-conversation-analysis','activity','{status}','Acao antiga','Contexto antigo','{{\"semanticIntentKey\":\"cotacao\"}}',now()-interval '2 days');")
    if incoming:
        message_status = "deleted" if deleted else "received"
        print(f"insert into whatsapp_messages(conversation_id,direction,status,message_at) values({uid(3)},'incoming','{message_status}',now()-interval '1 day');")
    print(bind(suggestion_sql, parameters))
    label = f"{status} with incoming={incoming} deleted={deleted}"
    check(f"(select status='{expected}' from ai_agent_suggestions where id={uid(10)})", label)
    if status == "accepted" and incoming:
        check("(select count(*)=2 and count(*) filter(where status='pending')=1 from ai_agent_suggestions)", "new message after accepted action creates new pending action")
    else:
        check("(select count(*)=1 from ai_agent_suggestions)", "deduplication keeps one suggestion")
    if status == "rejected" and incoming and not deleted:
        check(f"(select resolved_at is null and response_required_at is not null from ai_agent_suggestions where id={uid(10)})", "renewed rejection is visible with response warning")
print("rollback;")
