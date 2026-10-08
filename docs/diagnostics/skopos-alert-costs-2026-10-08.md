# Custo dos alertas e sugestões do Agent Skopos — 08/10/2026

O verificador de sugestões ficou **72,24% mais caro** na estimativa interna entre 05/10 e 07/10: US$ 2,9184 → US$ 5,0268 por dia. A prioridade é **agrupar reavaliações por sugestão/contato, usando uma janela configurável por empresa**, começando pela Staff M&B. Trocar o modelo não é a primeira medida: todas as chamadas faturáveis desse verificador já usaram `gpt-5.6-luna`.

Esta entrega contém diagnóstico e proposta de melhoria. Nenhum código de execução, configuração de empresa, serviço, fila ou dado de negócio foi alterado. Não houve deploy nem commit.

## Fontes e limites

Consultas realizadas em 08/10/2026, aproximadamente entre 13h52 e 14h01, no horário de São Paulo. Foram usados o servidor e o banco fornecidos. A conexão TCP ao host/porta foi validada; as consultas seguintes acessaram o mesmo `crmdb`, com `crm_user`, dentro do container PostgreSQL identificado no servidor.

- Comparação principal: dias completos **05/10 e 07/10**, de 00h00 a 00h00 do dia seguinte, com limite final exclusivo. 06/10 contém a transição de versão e aparece separadamente.
- 08/10: período parcial de 00h00 a **13h50**. Não deve ser comparado como se fosse um dia completo.
- Consumo: `ai_agent_invocation_logs`, incluindo entrada, saída e chamadas adicionais de recuperação. Entrada em cache já está incluída na entrada total; não foi somada novamente.
- Estimativa em dólar: tarifas de `ai_rate_cards`, cadastradas em 15/09. Para Luna: entrada US$ 0,20/milhão, saída US$ 1,20/milhão, entrada em cache US$ 0,02/milhão. Valores internos, sem validação da fatura ou da tabela atual do fornecedor.
- Créditos: `ai_usage_events`, apresentados separadamente. Créditos calculados, créditos efetivamente debitados e dólares do fornecedor são grandezas diferentes.
- Todas as transações foram somente de leitura, com timeout de 30 segundos e `ROLLBACK`. Consultas não exportaram mensagens ou transcrições de clientes nem credenciais.
- Volumes, empresas e evidências mudaram entre dias. A comparação descreve o comportamento observado e não isola causalmente a implantação. Configurações são o estado atual, não uma reconstrução histórica.

[SQL de reprodução](skopos-alert-costs-2026-10-08.sql).

## Onde o gasto aumentou

| Verificador de sugestões | 05/10 | 06/10 | 07/10 |
| --- | ---: | ---: | ---: |
| Execuções primárias com uso registrado | 1.487 | 2.607 | 2.119 |
| Chamadas adicionais de recuperação | 0 | 231 | 142 |
| Tokens totais, incluindo recuperação | 12.795.208 | 26.543.758 | 22.835.884 |
| Tokens por execução primária | 8.604,71 | 10.181,73 | 10.776,73 |
| Custo estimado | US$ 2,9184 | US$ 5,9023 | US$ 5,0268 |

Entre 05/10 e 07/10, as execuções primárias cresceram **42,50%**, e os tokens por execução, **25,24%**. Juntos, esses fatores explicam o aumento de **78,47%** nos tokens do verificador. Em 07/10, ele representou **57,07%** do custo estimado dos agentes presentes nos logs de invocação.

O custo total estimado desses agentes foi US$ 14,7473 → US$ 8,8084, principalmente pela queda de risco. Portanto, o aumento confirmado é o dos alertas/verificações; os dados não mostram aumento do total desses agentes nessa comparação. Esse total não abrange todos os serviços que registram consumo somente em `ai_usage_events`, como certas transcrições e atendimentos.

| Empresa | Verificador em 05/10 | Verificador em 07/10 | Variação | Total dos agentes nos logs em 07/10 |
| --- | ---: | ---: | ---: | ---: |
| Staff M&B | US$ 1,3340 | US$ 2,9340 | +119,95% | US$ 4,5880 |
| AVEP | US$ 0,5940 | US$ 1,1452 | +92,81% | US$ 2,3510 |
| Wagner Brum | US$ 0,7377 | US$ 0,6681 | −9,44% | US$ 0,7178 |
| Vetor Modular | US$ 0,1122 | US$ 0,1815 | +61,79% | US$ 0,2062 |
| Lumyne Estética Avançada | US$ 0,1378 | US$ 0,0814 | −40,90% | US$ 0,6944 |
| TRIASA | US$ 0,0028 | US$ 0,0165 | +487,26%, base pequena | US$ 0,0504 |
| Multiplix | US$ 0,0000 registrado | US$ 0,0000 registrado | Sem chamadas com uso | US$ 0,2007 |

Staff e AVEP somam **81,15%** do custo do verificador em 07/10. Na Staff, as execuções primárias passaram de 488 para 1.120; na AVEP, de 461 para 476, mas os tokens por execução passaram de 5.296 para 10.865. A Staff precisa sobretudo reduzir frequência; a AVEP precisa sobretudo revisar o contexto enviado.

Academia Digital Pro, Dominus Imóveis, EMPOWERMENT e MB SOLUCOES não tiveram consumo registrado do verificador nessa janela. Ausência de consumo não demonstra funcionamento correto; há falhas de credenciais em outros agentes dessas empresas.

## O que as otimizações atuais entregam

### Compactação e recuperação

Em 07/10 houve 1.791 chamadas compactas e 142 recuperações completas: **7,93%** das compactas precisaram de outra chamada. As recuperações acrescentaram **2.130.836 tokens**, **9,33%** do consumo do verificador, ou aproximadamente **US$ 0,4662** pelas tarifas internas.

| Empresa | Redução líquida de caracteres nas compactas | Recuperações / compactas | Tokens da recuperação / total do verificador |
| --- | ---: | ---: | ---: |
| Staff M&B | 7,37% | 51 / 917 = 5,56% | 7,00% |
| AVEP | 5,30% | 36 / 374 = 9,63% | 11,69% |
| Wagner Brum | 15,40% | 34 / 304 = 11,18% | 12,99% |
| Vetor Modular | 9,72% | 17 / 101 = 16,83% | 22,56% |
| Lumyne | 8,64% | 2 / 80 = 2,50% | 2,20% |
| TRIASA | 7,66% | 2 / 15 = 13,33% | 11,90% |

Caracteres não equivalem a tokens faturados. Os números mostram que ganhos pequenos de representação podem ser consumidos pela segunda passagem, mas não calculam o contrafactual exato de enviar tudo expandido uma única vez.

As 142 recuperações foram associadas à chamada compacta anterior da mesma sugestão, empresa e modelo, em até dois minutos. **74 mudaram o status da resposta**, incluindo 26 na Staff e 19 na AVEP. Não é seguro simplesmente remover a recuperação ou aceitar respostas de baixa confiança. A concordância de status também não substitui uma revisão da justificativa e da prova.

Os modos de seleção de evidências e compactação de reuniões não estavam configurados no ambiente do serviço; os padrões do código são `full`. O modo `full` do seletor mantém todo o conjunto coletado e ainda usa a compactação existente. **Não significa desabilitar a compactação do verificador.** Não houve piloto `shadow` habilitado no ambiente inspecionado.

### Cache e frequência

O cache reutiliza resultados `unfulfilled`/`inconclusive` quando contexto, política, modelo, instruções e estado temporal não mudam. A amostra limitada dos logs de serviço encontrou reutilizações; ela não constitui um contador completo de economia histórica.

Pedidos de IA estritamente idênticos foram poucos em 07/10: 17 repetições na Staff, duas em Wagner, duas em Lumyne e uma em Vetor. Isso representa **22 de 2.261 chamadas com uso**, menos de 1%. Um cache baseado apenas em requisições idênticas tem potencial limitado; os contextos estão mudando entre muitas verificações.

Há, entretanto, chamadas próximas para a mesma sugestão:

| Empresa | Execuções primárias / sugestões distintas | Intervalo anterior < 5 min | Intervalo anterior < 15 min |
| --- | ---: | ---: | ---: |
| Staff M&B | 1.120 / 441 | 105 | 215 |
| AVEP | 476 / 304 | 30 | 63 |
| Wagner Brum | 304 / 32 | 49 | 91 |
| Vetor Modular | 110 / 108 | 0 | 1 |
| Lumyne | 94 / 64 | 3 | 13 |
| TRIASA | 15 / 15 | 0 | 0 |

São candidatos a agrupamento, não chamadas automaticamente dispensáveis ou uma previsão de economia: uma evidência nova pode justificar a segunda avaliação.

No código, análises concluídas de WhatsApp, Instagram e reuniões chamam `SuggestionCompletionVerificationScheduler.RequestForScopeAsync`, que libera imediatamente as sugestões pendentes do contato/escopo. O campo **`debounce_minutes=5` do verificador não é utilizado para controlar esse agendamento**. Mudar apenas esse campo no banco não resolve a frequência. As reavaliações periódicas usam 15 minutos para pendências com prazo, 24 horas sem prazo e até 60 minutos para inconclusões/falhas. Novas análises podem antecipar essas esperas.

## Falhas operacionais e proteção de custo

Em 08/10, até 13h50, houve **25.786 tentativas**, **25.607 falhas** e apenas **179 chamadas com tokens registrados** nos logs de invocação. Os HTTP 429 examinados informam `credit_balance_exhausted`; há também erros locais de chave ausente. Esse bloqueio do provedor limita a carga processada. O custo pequeno desse período parcial não comprova economia da otimização.

Entre 07/10 00h00 e 08/10 13h50:

- **Multiplix:** 18.657 falhas do verificador por chave ausente; nenhuma chamada com uso registrado desse agente. A configuração do verificador e a de WhatsApp não têm chave própria; o fallback atual não busca a chave do Instagram.
- **Staff:** 5.725 falhas por saldo esgotado no verificador e 2.141 no WhatsApp.
- **AVEP:** 1.662 falhas por saldo esgotado no verificador e 1.163 no WhatsApp.
- **Wagner Brum:** 377 falhas locais de chave no Instagram. A configuração desse agente está com intervalo de um minuto.
- **Vetor Modular:** 231 falhas por saldo no checkout. Esse custo operacional é distinto do custo dos alertas.

Erros sem tokens registrados não foram convertidos em gastos fictícios. Ainda assim, produzem consultas, histórico e tentativas sem utilidade. Corrigir a chave da Multiplix restabelecerá verificações e **aumentará** o consumo faturável; deve vir acompanhada de controle de frequência.

O serviço usa `Saas__AiCreditMeteringEnabled=true` e `Saas__AiCreditEnforcementEnabled=false`. Em 07/10, o verificador registrou 14.789 créditos calculados para Staff e 5.761 para AVEP, ambos em `shadow`. Multiplix, Wagner, Vetor e TRIASA aparecem como `unlimited`. Esses modos medem consumo, mas não representam um teto efetivo de gasto do fornecedor. Todas as tarifas de créditos atualmente cadastradas usam os mesmos pesos por modelo, apesar das diferentes estimativas em dólar. Não comparar créditos como se fossem dólares.

Disponibilidade dos processos: IA 3/3, consumers 2/2, API/frontend/webhooks 1/1; container PostgreSQL saudável. A imagem de IA é `7838921b281e49c5be4b00b49e53a3bde262a6be`, igual ao HEAD local examinado. Uma amostra limitada dos logs contém quatro linhas relacionadas a deadlock; isso é uma observação operacional, não causa comprovada do aumento de tokens, e não foi objeto de alteração.

## Proposta pontual de melhoria

### 1. Prioridade: agendamento agrupado do verificador

Implementar exclusivamente em `SuggestionCompletionVerificationScheduler` uma espera configurável por empresa para solicitações causadas por novas análises. Durante a espera, agrupar novos eventos da mesma sugestão e verificar o contexto mais recente uma vez. Respeitar a lease de sugestões em processamento e limitar o adiamento máximo para evitar espera indefinida em conversas movimentadas.

Proposta inicial: piloto na **Staff com janela de 5 minutos**, ampliando para **15 minutos** se o tempo de baixa de alertas continuar aceitável; Wagner é o próximo candidato, com 304 execuções para apenas 32 sugestões. Os 215 intervalos menores que 15 minutos da Staff equivalem a 19,20% de suas execuções primárias, mas não são uma promessa de redução nessa proporção.

Manter intactos a publicação imediata de pedido de resposta do cliente, os modelos, a coleta de evidências, as notificações, a validação de cumprimento e as verificações periódicas. O agrupamento pode atrasar a confirmação de cumprimento; isso deve ser medido explicitamente. Falhas de configuração/saldo precisam respeitar uma espera própria, sem serem liberadas repetidamente por eventos novos.

Para implementar, será necessário conectar `debounce_minutes` ao scheduler; **esta proposta ainda não está implementada**. O padrão e a ativação por empresa devem permitir preservar o comportamento das demais empresas.

### 2. Tornar a compactação economicamente seletiva

Hoje o cliente compacta se economizar qualquer quantidade líquida de caracteres. Acrescentar um limiar mínimo e uma opção por empresa para usar diretamente o conjunto completo expandido quando o ganho for pequeno. Começar a avaliação por AVEP e Vetor, onde a recuperação representa 11,69% e 22,56% dos tokens do verificador.

O caminho direto completo conserva todas as evidências e a validação existente. Sua economia líquida e qualidade ainda precisam de comparação controlada; não basta assumir que eliminar a segunda chamada compensa o aumento de entrada na primeira. Preservar a recuperação nos casos em que a compactação continuar habilitada. Não ativar seleção de evidências em produção sem revisar resultados e provas; o modo `shadow` adiciona custo durante a avaliação.

### 3. Ajustes de configuração por empresa

| Empresa | Proposta |
| --- | --- |
| Staff M&B | Priorizar piloto de agrupamento do verificador. WhatsApp está em 10 min; testar 15–20 min se a latência comercial permitir. Instagram está em 1 min, mas não teve consumo faturável nessa janela: não é o principal gasto observado. |
| AVEP | Manter os 30 min de WhatsApp já configurados. Investigar as evidências grandes do verificador e experimentar o caminho expandido direto nos contextos com ganho mínimo. |
| Wagner Brum | Agrupar o verificador e revisar sugestões antigas ainda pendentes; 304 execuções para 32 sugestões. Corrigir a chave do Instagram antes de avaliar uma janela maior. |
| Multiplix | Configurar uma credencial válida para o verificador, específica da empresa ou pelo fallback autorizado, e aplicar espera para erro de configuração. Instagram já usa Luna com 10 min e não explica o aumento do verificador. |
| Vetor Modular | Priorizar redução de recuperações, não agrupamento: só uma execução ocorreu a menos de 15 min da anterior. WhatsApp/Instagram usam Terra, mas houve apenas uma chamada faturável de WhatsApp em 07/10; trocar modelo não resolve o principal gasto observado. |
| Lumyne | Manter Luna e os 10 min do WhatsApp inicialmente. O custo do verificador caiu; não priorizar alteração nessa empresa. |
| TRIASA | Manter configuração inicialmente; custo absoluto do verificador é baixo. Tratar saldo e reavaliações de falha. |
| Academia, Dominus, EMPOWERMENT, MB SOLUCOES | Revisar agentes ativos sem credencial e reduzir tentativas locais sem utilidade. Não atribuir economia de tokens às falhas. |

### 4. Medir o resultado do piloto

Comparar pelo menos dois dias úteis completos, por empresa, mantendo o mesmo modelo. Registrar eventos recebidos/agrupados, acertos de cache, execuções determinísticas, chamadas primárias/recuperações, tokens e dólares por sugestão, tempo até confirmar cumprimento, falhas e resultados revisados.

Critérios: reduzir custo total por sugestão sem aumentar falsos cumprimentos ou deixar evidências novas sem análise, e manter o atraso dentro da janela combinada. Evitar ampliar rollout com base apenas em caracteres ou número de chamadas isoladas. Os valores de economia continuam hipóteses até a execução desse piloto.

## Encerramento

Somente este documento e o SQL de leitura foram acrescentados localmente ao repositório `crm-ai`. Não há alteração dos demais repositórios. As sessões de consulta ao banco foram encerradas ao terminar cada comando. A conferência final confirmou `transaction_read_only=on` e zero outras sessões `psql` no banco; o comando dessa conferência também terminou com `ROLLBACK` e encerrou sua conexão. Credenciais não foram gravadas nestes arquivos.
