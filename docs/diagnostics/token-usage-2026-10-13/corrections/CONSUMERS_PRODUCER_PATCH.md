# Contrato da correção do produtor

Arquivos alterados, ambos limpos antes desta tarefa:
- CONSUMERS_CRM/src/CrmRestApi.Consumers.Worker/Consumers/WhatsappConversationAnalysisConsumer.cs
- CONSUMERS_CRM/src/CrmRestApi.Consumers.Worker/Consumers/WhatsappAudioTranscriptionConsumer.cs (apenas comentário, comportamento preservado)

Teste novo:
- CONSUMERS_CRM/tests/CrmRestApi.Consumers.Tests/ConversationAnalysisDeferredDatabaseTests.cs

O produtor não reclama novamente conversas WhatsApp ou Instagram com last_analysis_status='deferred'. O worker crm-ai conserva essa entrega em RabbitMQ e a retomará após TTL; recuperação periódica continua limitada a runs processing/queued. A recuperação de órfãos WhatsApp passa a considerar um run deferred como trabalho ativo.

No Instagram, a gravação queued após publicação somente ocorre se a conversa ainda estiver processing, evitando sobrescrever deferred ou completed gravado pelo crm-ai antes do retorno da publicação. O caminho normal processing→queued continua igual.

Transcrição nova pode alterar deferred→requested, conforme o comportamento anterior. Isso permite capturar uma nova versão do payload que contém a transcrição pronta; o guard do crm-ai controla o momento da chamada ao provedor. O callback de defer do crm-ai deve preservar requested. Runtime e supersession dos runs são responsabilidade da alteração em crm-ai, coordenada com o root/failure_retry.

Validação: 4 testes reais de métodos dos produtores aprovados, 0 falhas, 0 skips, usando PostgreSQL local na porta 55439, tabelas pg_temp e DML em transaction_read_only=on; nenhum schema persistente, RabbitMQ ou provedor é chamado. TRX: consumers-tests/conversation-deferred.trx. Build do projeto de testes/worker aprovado. Uma warning CS8604 preexistente em AutomationFollowupDatabaseTests.cs permanece; não foi alterada. git diff --check dos arquivos da tarefa aprovado.

Casos testados: deferred excluído e failed mantendo retry; run deferred retido como ativo para órfão, recuperação normal de queued preservada; queued do dispatcher não sobrescreve deferred/completed do Instagram; áudio pronto durante defer gera requested e novo payload com transcrição. Nenhum arquivo anteriormente modificado pelo usuário foi editado.
