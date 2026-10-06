# Diagnóstico de sugestões de atendimento — AVEP

Verificado em 06/10/2026. Empresa: `214b614e-f39a-4cb7-b975-b53207d5ebef`.

## Resultado

O prompt WhatsApp da AVEP foi corrigido no banco. Foram recuperadas 17 conversas com claims antigos sem execução ativa: 3 tinham o trecho mais recente já processado e 14 ficaram elegíveis para nova análise. Depois da recuperação, a consulta de claims antigos sem execução ativa retornou zero.

Andre Jorge, Jaqueline S. Silva e Jocimar Dias foram reanalisados pelo fluxo real dos serviços. Os três terminaram com `shouldCreateActivity=true`, `requiresSellerResponse=true`, sugestão `pending`, `response_required_at` preenchido e uma notificação `activity_suggestion_response_pending` para o responsável. Nenhuma mensagem foi enviada aos clientes.

As correções de código estão locais, sem commit e sem implantação. Os serviços continuam nas imagens anteriores; a prevenção permanente das corridas, claims órfãos e reativação de sugestões depende de publicar `crm-ai` e `consumers`.

## Fluxo verificado

1. Webhooks/consumers persistem mensagens e atualizam a conversa.
2. `WhatsappConversationAnalysisConsumer` seleciona conversas após inatividade, resolve contato/oportunidade, cria execução e publica o batch no RabbitMQ. O caminho de contato sem oportunidade também está ativo.
3. `crm-ai` carrega configuração por empresa, resumo incremental, sugestões existentes e scorecard; chama o provedor.
4. `PostgresWhatsappConversationActionStore` grava o resultado, deduplica sugestões, calcula a necessidade de resposta pela última mensagem real e conclui a execução/checkpoint.
5. A API consulta sugestões `pending` e sinaliza `awaiting_response` quando `response_required_at` está preenchido. O worker de verificação publica a notificação para o responsável.
6. O frontend usa esses campos nas listas de atendimento e atividades. Não houve alteração no frontend nem nos contratos da API. A validação final foi no banco/serviços, sem inspeção visual de uma sessão autenticada.

## Causas confirmadas

| Problema | Evidência | Correção |
|---|---|---|
| Critério de resposta limitado à pergunta explícita da última mensagem | Andre pediu cotação, a equipe pediu identificação e o cliente enviou a placa. A análise anterior retornou nenhuma atividade porque não havia outra pergunta nem prazo explícito. | Continuidade após envio dos dados solicitados passa a ser atendimento pendente, sem exigir outra pergunta ou prazo. |
| Saudações de abertura/retomada dispensadas | Jocimar abriu/retomou o contato com saudação e a análise anterior retornou nenhuma atividade. | Distinguir abertura sem atendimento de encerramento/agradecimento sem pendência. |
| Sugestão existente sem sinal de resposta | Jaqueline enviou os dados solicitados; a análise sugeriu continuar a cotação, mas retornou `requiresSellerResponse=false`. | Deduplicar a sugestão existente mantendo a sinalização de atendimento pendente. |
| Corrida entre conclusão da IA e confirmação do dispatcher | Conversas `queued` tinham execução mais recente `completed`; timestamps de confirmação eram posteriores à conclusão por milissegundos. | Atualizações condicionais impedem retornar uma execução concluída para `queued`. |
| Recuperação não cobria claim sem execução ativa | 17 conversas antigas estavam `processing`/`queued` sem run ativo. | Recuperação de claims órfãos preserva o checkpoint concluído e mantém runs ativos/frescos intactos. |
| Nova ação deduplicada contra sugestão rejeitada continuava oculta | Consulta e código confirmaram sugestões com resultado novo gravado após `resolved_at`, mas ainda `rejected`. Exemplo histórico: Maurício, confirmação de elegibilidade em setembro depois de rejeição em agosto. | Reabrir como `pending` somente com mensagem nova do cliente posterior à rejeição; preservar rejeição quando não há fato novo. |
| Imagens/documentos sem texto eram descartados | `LoadNewMessagesAsync` filtrava mensagens pelo texto/transcrição. | Preservar registro de recebimento de imagem/documento/vídeo, sem inventar conteúdo. Áudio continua aguardando transcrição. Mensagens apagadas são excluídas. |
| Resultado parcialmente persistido podia impedir retry | O caminho de oportunidade usava existência de insight como confirmação de conclusão, antes do checkpoint final do run. | Para batches com `runId`, verificar conclusão da execução; manter comportamento anterior para eventos legados sem run. |

## Configuração e serviços

- Análise WhatsApp e verificação de sugestões da AVEP estavam ativas e com chave configurada.
- Intervalo de inatividade: **30 minutos** no agente e na configuração da conversa; polling de 60 segundos, batch de 10 por consumer e retry de 15 minutos. O intervalo foi preservado: é uma configuração válida, embora introduza até aproximadamente 31 minutos antes da análise automática normal.
- Prompt personalizado tinha texto com codificação corrompida (`VocÃª`, etc.) e foco restrito em ação futura explícita. Foi restaurada a codificação e acrescentada prioridade de atendimento, preservando o contexto comercial, credenciais e intervalo.
- No período consultado de sete dias, houve **68 falhas HTTP 429 por saldo esgotado no provedor**: 37 em 01/10 e 31 em 05/10, além de uma falha 503. As chamadas recentes e as três reanálises concluíram com sucesso. Crédito interno do CRM não substitui saldo do provedor.
- Réplicas em execução na verificação inicial: API 1/1, frontend 1/1, consumers 2/2 e IA 3/3. Postgres estava saudável; RabbitMQ em execução.
- Logs de falta de chave em outros agentes/empresas foram observados, mas não representam falta de chave do agente WhatsApp da AVEP e não motivaram alteração fora deste escopo.

## Aplicado no banco

- [Correção do prompt](avep-whatsapp-prompt-2026-10-06.sql): restrita ao agente WhatsApp da AVEP, com condição de versão para evitar sobrescrever edição concorrente. Aplicada a um registro.
- [Recuperação de claims antigos](avep-recover-orphaned-analyses-2026-10-06.sql): restrita à AVEP e a conversas sem run ativo. Aplicada a 17 conversas.
- Reanálise limitada aos três exemplos confirmados. Checkpoint recuado somente ao início do último trecho concluído, preservando resumo e histórico.
- [Rollback do prompt](avep-whatsapp-prompt-rollback-2026-10-06.sql): condicionado ao conteúdo aplicado para preservar edições posteriores. Não executado.

## Validação

- `crm-ai`: 213 testes aprovados, 11 testes de infraestrutura já existentes ignorados pela suíte.
- `consumers`: 66 testes aprovados. A suíte original não compila por erro preexistente em `WhatsappMessageReceiptTests.cs:30` (`CS0453` em FluentAssertions). Esse arquivo foi excluído somente na execução por um target temporário em `/tmp`; o arquivo e o projeto não foram modificados.
- 24 verificações SQL passaram usando as consultas reais do código e tabelas temporárias, com rollback. Cobrem conclusão antes/entre updates do dispatcher, recuperação sem run, preservação de runs ativos e claims frescos, checkpoints, rejeição com/sem nova mensagem, mensagem apagada, deduplicação e nova pendência após ação aceita.
- Novos testes de payload confirmam regras WhatsApp com prompt personalizado e preservação das regras anteriores de Instagram. Novos testes do consumer cobrem anexos sem legenda e dependência de transcrição para áudio.
- `git diff --check` sem erros nos dois repositórios alterados. Alterações preexistentes de relatórios no frontend foram preservadas.
- A connection string fornecida foi validada com host, porta, banco, usuário e senha informados. Cada sessão PostgreSQL foi encerrada ao terminar a chamada; a conexão SSH de diagnóstico foi encerrada ao finalizar.

Para repetir as verificações SQL sem registrar credenciais:

```sh
python3 tests/verify_whatsapp_attendance_sql.py --consumers-root /caminho/CONSUMERS_CRM | psql ... -v ON_ERROR_STOP=1
```

O script exige o schema atual do CRM para copiar a definição de sugestões. Toda escrita da verificação ocorre em tabelas temporárias, seguida de rollback.
