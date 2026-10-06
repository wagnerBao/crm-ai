-- Recupera apenas claims antigos da AVEP sem execução ativa.
begin;
set local lock_timeout='5s';
set local statement_timeout='30s';
            with orphaned as (
                select conversation.id,
                       greatest(conversation.last_analyzed_message_at, (
                           select max(completed.window_end_at)
                           from whatsapp_conversation_analysis_runs completed
                           where completed.conversation_id = conversation.id
                             and completed.status = 'completed'
                       )) as checkpoint
                from whatsapp_conversations conversation
                where conversation.company_id = '214b614e-f39a-4cb7-b975-b53207d5ebef'::uuid
                  and conversation.last_analysis_status in ('processing', 'queued')
                  and coalesce(conversation.last_analysis_at, conversation.updated_at)
                      <= now() - make_interval(mins => 15)
                  and not exists (
                      select 1 from whatsapp_conversation_analysis_runs active_run
                      where active_run.conversation_id = conversation.id
                        and active_run.status in ('processing', 'queued')
                  )
                for update of conversation skip locked
            )
            update whatsapp_conversations conversation
            set last_analyzed_message_at = orphaned.checkpoint,
                last_analysis_status = case
                    when orphaned.checkpoint >= conversation.last_message_at then 'completed'
                    else 'failed'
                end,
                updated_at = now()
            from orphaned
            where conversation.id = orphaned.id
returning conversation.id, conversation.last_analysis_status;
commit;
