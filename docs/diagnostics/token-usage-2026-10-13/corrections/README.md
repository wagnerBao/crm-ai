# Correções de consumo e retomada dos agentes — 13/10/2026

O diagnóstico atualizado está em [CURRENT_STATE.md](CURRENT_STATE.md), com corte às **12:07:36 BRT**. Hoje foram medidos **19.561.532 tokens em 2.532 chamadas**, com **US$ 5,5204 estimados pelas tarifas internas**, sem conciliação com a fatura. Na última hora houve **575 falhas por falta de chave**: Multiplix/verificador (551), Wagner Brum/Instagram (20) e Academia Digital Pro/Instagram (4). Não houve falha de saldo nessa hora.

## Alterações locais

| Problema observado | Correção | Comportamento esperado |
| --- | --- | --- |
| 513/513 chamadas de WhatsApp repetem o contexto adicional nas instruções | O cliente compartilhado WhatsApp/Instagram omite somente uma cópia que seja exatamente igual a um bloco completo das instruções atuais | O contexto aparece uma vez. Conteúdo distinto, parcial ou de uma configuração antiga continua no pedido; o input original é preservado |
| Chave ausente/inválida ou quota esgotada provoca tentativas repetidas | Bloqueio persistente por empresa e agente, com fingerprint da configuração, intervalo de uma hora e tentativa única após o vencimento | Chave ausente é detectada antes da chamada e não gera histórico de falha de provedor; uma falha real continua registrada. Alteração da configuração libera o bloqueio no próximo processamento |
| Falhas de chave ou saldo descartam ou substituem o trabalho aguardando análise | Adiamento do evento RabbitMQ com confirmação de publicação antes do ACK; scheduler legado preserva seu payload e a data de retomada nesses adiamentos | O evento pode ser processado após a recuperação. Identidade, tipo, data e conteúdo do evento são mantidos |
| Eventos antecipam falhas ou provocam verificações imediatas em rajadas | Scheduler usa o debounce do verificador configurado para a empresa, com fallback global e padrão de cinco minutos; mantém o primeiro prazo, inclusive vencido | Eventos novos respeitam o prazo após falhas e a janela em andamento. Eventos durante uma chamada preservam o lease e continuam agendados após sua conclusão |
| Recovery do produtor recria lotes enquanto o original aguarda na fila | Estado operacional `deferred`, gravado após confirmação de publicação e antes do ACK, é respeitado pelo produtor | O lote aguarda sua retomada. O registro de despacho não sobrescreve esse estado, e áudio recém-transcrito ainda pode gerar um lote com texto atualizado |
| Entrega repetida ou lote antigo pode consumir tokens e sobrescrever contexto atualizado | Preflight ignora runs concluídos/substituídos; conclusão de um run novo substitui somente versões mais antigas da mesma empresa, conversa e mensagem final | Um lote antigo não descarta o lote mais recente com áudio atualizado. Outras janelas continuam pendentes |

O bloqueio guarda hash, motivo sanitizado e datas; não guarda chave ou corpo de resposta do provedor. Empresa, agente e fingerprint formam sua chave: configurações antigas não removem bloqueios de uma credencial nova. Uma chamada concorrente bem-sucedida não pode apagar um bloqueio novo. A retomada após vencimento usa uma permissão identificada para que somente a tentativa proprietária libere seu bloqueio. Bloqueios de configurações anteriores são preservados e não impedem o processamento com outra configuração.

Os fluxos protegidos nesta rodada são WhatsApp, Instagram e verificação de sugestões. A alteração do produtor está no repositório `CONSUMERS_CRM`; o restante está em `crm-ai`.

A persistência WhatsApp é serializada por empresa e conversa somente durante a gravação, após a resposta do modelo; o estado do run é verificado novamente sob o lock. As transações existentes nos helpers foram mantidas: o Npgsql não permite transações aninhadas. [Documentação do Npgsql](https://www.npgsql.org/doc/basic-usage.html#transactions).

## Verificação

Os testes utilizam clientes sintéticos e PostgreSQL local isolado. As consultas de diagnóstico de produção são somente leitura. Não foram alteradas configurações de agentes nem executadas chamadas a clientes reais.

| Validação | Resultado | Evidência |
| --- | --- | --- |
| Suíte completa `crm-ai` | 304 aprovados, zero falhas, 4 ignorados; 308 testes no total | [crm-ai-final.trx](validation/crm-ai-final.trx) |
| Integração do produtor `CONSUMERS_CRM` com PostgreSQL | 4 aprovados, zero falhas ou testes ignorados | [conversation-deferred.trx](validation/conversation-deferred.trx) |
| Revisão independente | Bloqueador de gravação concorrente resolvido; nenhum novo bloqueador identificado | Rechecagem de `completed/superseded` sob lock, isolamento por empresa/conversa e liberação em falhas/cancelamentos |
| Formatação dos patches | `git diff --check` sem erros nos dois repositórios, limitado aos arquivos desta correção no produtor | Verificação local |

Os quatro testes ignorados dependem de RabbitMQ real. Não há broker local disponível, e o daemon Docker não está acessível. Os testes unitários da publicação adiada passaram, cobrindo confirmação antes do ACK, falhas de publicação/gravação, preservação do evento e topologia; a entrega com broker real continua pendente antes da publicação. Os testes de PostgreSQL foram executados em um servidor isolado, encerrado após a validação.

A suíte do produtor foi limitada aos quatro testes desta mudança; não representa validação completa das outras alterações locais já existentes nesse repositório. Nenhuma chave de provedor ou conexão de produção foi usada pelos testes.

## Publicação e acompanhamento

As alterações estão no código local. Não houve commit, deploy ou aplicação de migração em produção. **Publicar primeiro o produtor `CONSUMERS_CRM` com suporte a `deferred`, depois o `crm-ai`**, para que a recuperação do produtor reconheça os eventos retidos pelo worker. A migração `database/20261013_create_ai_invocation_cooldowns.sql` é idempotente e também é carregada na inicialização do worker, antes dos consumidores. Não foi possível comprovar o SHA atualmente publicado.

Os três agentes sem chave continuam dependendo de configuração válida. O bloqueio evita insistência, mas não cria uma credencial. Na retomada, eventos adiados voltam pelo prazo da fila ou do scheduler; uma alteração de configuração é observada no próximo processamento, não implica execução imediata de toda a fila.

Após publicar, comparar períodos equivalentes e separar variação de volume de melhoria de eficiência:

1. Falhas por chave e quota por empresa/agente; devem cair após o primeiro bloqueio.
2. Tokens de entrada por chamada em WhatsApp/Instagram e ocorrência de contexto exatamente duplicado; esta ocorrência deve ser zero para as configurações atuais.
3. Chamadas e tokens por sugestão verificada, com atenção às rajadas abaixo de cinco minutos.
4. Tempo de retomada e idade do trabalho pendente; redução de chamadas não pode ser obtida deixando eventos presos.
5. Resultado, confiança e fallback do verificador. A seleção ampla de evidências e o scorecard ainda merecem avaliação com casos reais antes de reduzir conteúdo.

Não há economia em produção medida nesta rodada. Os valores de caracteres repetidos demonstram duplicação, mas não permitem converter diretamente para tokens ou dólares.
