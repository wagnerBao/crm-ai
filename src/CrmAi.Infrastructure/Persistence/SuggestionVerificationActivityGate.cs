namespace CrmAi.Infrastructure.Persistence;

/// <summary>Completed checks wait for new evidence, rather than another timer tick.</summary>
internal static class SuggestionVerificationActivityGate
{
    // Correlated with the claim query's "suggestion" row. The start of the
    // last check is used so an interaction arriving during an AI call survives.
    internal const string Sql = """
        and (
            suggestion.last_verified_at is null
            or suggestion.verification_status not in ('unfulfilled', 'inconclusive')
            or (
                suggestion.verification_status = 'unfulfilled'
                and suggestion.suggested_due_at <= now() - interval '5 minutes'
                and suggestion.priority_at is null
            )
            or exists (
                with contact_scope as (
                    select id, account_id from contacts
                    where id = suggestion.contact_id and company_id = suggestion.company_id
                ), related_opportunities as (
                    select distinct opportunity.id
                    from opportunities opportunity
                    left join opportunity_contacts relation on relation.opportunity_id = opportunity.id
                    cross join contact_scope contact
                    where opportunity.company_id = suggestion.company_id
                      and (relation.contact_id = suggestion.contact_id or opportunity.account_id = contact.account_id)
                ), changed_evidence as (
                    select 1 from activities activity
                    where activity.company_id = suggestion.company_id
                      and activity.activity_type is distinct from 'agent-skopos'
                      and (activity.contact_id = suggestion.contact_id
                           or activity.opportunity_id in (select id from related_opportunities))
                      and greatest(activity.updated_at, activity.created_at) >= suggestion.created_at
                      and greatest(activity.updated_at, activity.created_at) > suggestion.last_verified_at

                    union all
                    select 1 from whatsapp_messages message
                    inner join whatsapp_conversations conversation on conversation.id = message.conversation_id
                    where conversation.company_id = suggestion.company_id
                      and conversation.contact_id = suggestion.contact_id
                      and message.message_at >= suggestion.created_at
                      and (
                          greatest(message.created_at, message.updated_at, message.message_at) > suggestion.last_verified_at
                          or exists (
                              select 1 from whatsapp_message_audio_transcriptions audio
                              where audio.whatsapp_message_id = message.id and audio.status = 'ready'
                                and audio.updated_at > suggestion.last_verified_at
                          )
                      )

                    union all
                    select 1 from instagram_messages message
                    inner join instagram_conversations conversation on conversation.id = message.conversation_id
                    where conversation.company_id = suggestion.company_id
                      and conversation.contact_id = suggestion.contact_id
                      and message.message_at >= suggestion.created_at
                      and greatest(message.created_at, message.updated_at, message.message_at) > suggestion.last_verified_at

                    union all
                    select 1 from notes note
                    cross join contact_scope contact
                    where note.company_id = suggestion.company_id
                      and (note.contact_id = suggestion.contact_id or note.account_id = contact.account_id
                           or note.opportunity_id in (select id from related_opportunities))
                      and note.created_at >= suggestion.created_at
                      and greatest(note.updated_at, note.created_at) > suggestion.last_verified_at

                    union all
                    select 1 from opportunities opportunity
                    where opportunity.company_id = suggestion.company_id
                      and opportunity.id in (select id from related_opportunities)
                      and greatest(opportunity.updated_at, opportunity.created_at) >= suggestion.created_at
                      and greatest(opportunity.updated_at, opportunity.created_at) > suggestion.last_verified_at
                      and (
                          opportunity.created_at > suggestion.last_verified_at
                          or not exists (
                              select 1 from jsonb_array_elements(suggestion.verification_evidence) previous
                              where previous->>'id' = 'opportunity:' || opportunity.id::text
                          )
                          or exists (
                              select 1 from jsonb_array_elements(suggestion.verification_evidence) previous
                              where previous->>'id' = 'opportunity:' || opportunity.id::text
                                and previous->>'summary' is distinct from concat_ws(' | ', opportunity.name, opportunity.status)
                          )
                      )

                    union all
                    select 1 from opportunity_history history
                    where history.company_id = suggestion.company_id
                      and history.opportunity_id in (select id from related_opportunities)
                      and history.created_at >= suggestion.created_at
                      and history.created_at > suggestion.last_verified_at
                      and history.event not like 'Atividade criada automaticamente pelo Agent Skopos%'

                    union all
                    select 1 from meeting_audio_recordings recording
                    left join activities activity on activity.id = recording.activity_id
                    where recording.company_id = suggestion.company_id
                      and (activity.contact_id = suggestion.contact_id
                           or recording.opportunity_id in (select id from related_opportunities))
                      and coalesce(recording.transcribed_at, recording.updated_at, recording.created_at) >= suggestion.created_at
                      and greatest(recording.transcribed_at, recording.updated_at, recording.created_at) > suggestion.last_verified_at
                )
                select 1 from changed_evidence
            )
        )
        """;
}
