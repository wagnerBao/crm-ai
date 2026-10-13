# Consumo de tokens dos agentes CRM Skopos

**Data de referência: 13/10/2026. Corte do consumo: 11h00, America/Sao_Paulo.**

Os agentes registraram **268.670.847 tokens em 15 dias**, com **US$ 104,9002 de custo estimado pelas tarifas cadastradas**. O verificador de sugestões concentra 64,70% dos tokens. A análise de risco representa 32,04% do custo com apenas 5,81% dos tokens.

O problema mais urgente é a disponibilidade: **352.508 falhas em 382.371 tentativas do crm-ai (92,19%)**. Entre 10 e 12/10, todas as 124.280 tentativas falharam, sem tokens nem sucessos registrados. Esses dias não comprovam economia. Hoje houve retomada de chamadas bem-sucedidas, mas a taxa de falha ainda foi 90,71%.

Os períodos incluem hoje parcial e se sobrepõem. Não somar as três linhas. “Chamadas registradas” inclui os eventos de backend/consumers e as chamadas do crm-ai. As taxas de falha usam exclusivamente os logs de tentativas do crm-ai, cujo universo é diferente.

## Visão geral

| Período até 11h de 13/10 | Chamadas registradas | Tokens entrada + saída | Custo estimado | Tentativas crm-ai | Falhas crm-ai |
| --- | --- | --- | --- | --- | --- |
| Hoje (13/10) | 2.258 | 17.366.507 | US$ 4,6946 | 22.271 | 90,71% |
| 7 dias (07 a 13/10) | 11.768 | 92.279.454 | US$ 27,2197 | 216.958 | 94,92% |
| 15 dias (29/09 a 13/10) | 33.065 | 268.670.847 | US$ 104,9002 | 382.371 | 92,19% |

| Período | Entrada | Saída | Entrada em cache | Cache / entrada | Tokens por chamada |
| --- | --- | --- | --- | --- | --- |
| Hoje | 16.382.561 | 983.946 | 2.974.642 | 18,16% | 7.691 |
| 7 dias | 86.910.309 | 5.369.145 | 13.491.231 | 15,52% | 7.842 |
| 15 dias | 254.646.709 | 14.024.138 | 33.835.253 | 13,29% | 8.126 |

Entrada representa 94,78% dos tokens em 15 dias. O foco de redução é o contexto enviado. Cache é uma parcela da entrada e não é somado novamente. Tokens de raciocínio já fazem parte da saída quando informados.

## Consumo por agente

| Agente | Tokens hoje | USD hoje | Tokens 7 dias | USD 7 dias | Tokens 15 dias | USD 15 dias |
| --- | --- | --- | --- | --- | --- | --- |
| Verificador de sugestões | 10.991.732 | US$ 2,3780 | 60.139.822 | US$ 13,1877 | 173.819.789 | US$ 37,7541 |
| Análise de WhatsApp | 3.778.239 | US$ 1,1342 | 22.353.039 | US$ 6,5652 | 58.211.095 | US$ 20,8808 |
| Análise de risco | 157.249 | US$ 0,3506 | 1.645.210 | US$ 3,7455 | 15.597.574 | US$ 33,6117 |
| Análise de Instagram | 2.205.040 | US$ 0,3659 | 6.955.912 | US$ 1,1286 | 15.438.308 | US$ 2,5841 |
| Checkout diário | 151.943 | US$ 0,3712 | 557.581 | US$ 1,3557 | 2.542.502 | US$ 5,9007 |
| Atendimento WhatsApp (consumer) | 0 | US$ 0,0000 | 181.620 | US$ 0,0208 | 1.517.437 | US$ 0,1696 |
| Skopos Coach | 0 | US$ 0,0000 | 175.958 | US$ 0,4992 | 684.278 | US$ 1,9046 |
| Transcrição de áudio WhatsApp | 39.378 | US$ 0,0898 | 153.467 | US$ 0,3524 | 620.936 | US$ 1,4237 |
| Transcrição/análise de atendimento | 0 | US$ 0,0000 | 56.695 | US$ 0,3542 | 136.654 | US$ 0,5734 |
| Atendimento WhatsApp (runtime) | 42.926 | US$ 0,0049 | 51.765 | US$ 0,0077 | 51.765 | US$ 0,0077 |
| assistant.answer | 0 | US$ 0,0000 | 0 | US$ 0,0000 | 34.538 | US$ 0,0826 |
| whatsapp.activity-message | 0 | US$ 0,0000 | 5.668 | US$ 0,0014 | 12.014 | US$ 0,0029 |
| service-agent.instagram | 0 | US$ 0,0000 | 2.717 | US$ 0,0011 | 2.717 | US$ 0,0011 |
| assistant.intent | 0 | US$ 0,0000 | 0 | US$ 0,0000 | 1.240 | US$ 0,0031 |

| Agente nos 15 dias | Chamadas | Parcela dos tokens | Parcela do custo | Tokens por chamada |
| --- | --- | --- | --- | --- |
| Verificador de sugestões | 17.271 | 64,70% | 35,99% | 10.064 |
| Análise de WhatsApp | 6.263 | 21,67% | 19,91% | 9.294 |
| Análise de risco | 435 | 5,81% | 32,04% | 35.856 |
| Análise de Instagram | 5.782 | 5,75% | 2,46% | 2.670 |
| Checkout diário | 68 | 0,95% | 5,63% | 37.390 |
| Atendimento WhatsApp (consumer) | 532 | 0,56% | 0,16% | 2.852 |
| Skopos Coach | 38 | 0,25% | 1,82% | 18.007 |

O verificador é a prioridade de volume. Risco é a prioridade monetária: 435 chamadas, média de 35.856 tokens, US$ 33,6117 e nenhum cache registrado. O contexto e a seleção de modelo precisam ser avaliados por tarefa; uma redução proporcional de tokens não produz a mesma redução em dólares em todos os agentes.

## Consumo por empresa

| Empresa | Tokens hoje | USD hoje | Tokens 7 dias | USD 7 dias | Tokens 15 dias | USD 15 dias |
| --- | --- | --- | --- | --- | --- | --- |
| Staff M&B | 7.900.772 | US$ 1,9742 | 51.637.776 | US$ 14,5384 | 114.378.849 | US$ 40,8479 |
| AVEP | 4.435.620 | US$ 1,3892 | 18.786.452 | US$ 6,9488 | 84.698.738 | US$ 44,1310 |
| Wagner Brum | 1.510.316 | US$ 0,3445 | 6.442.475 | US$ 1,4937 | 20.954.315 | US$ 4,8329 |
| Lumyne Estética Avançada | 1.128.196 | US$ 0,4067 | 5.515.495 | US$ 1,8965 | 16.801.889 | US$ 6,2332 |
| Multiplix | 2.209.948 | US$ 0,3853 | 6.963.985 | US$ 1,1817 | 15.468.108 | US$ 2,8051 |
| Vetor Modular | 65.561 | US$ 0,1619 | 2.262.543 | US$ 0,9805 | 13.871.447 | US$ 5,5064 |
| Dominus Imóveis | 42.926 | US$ 0,0049 | 224.546 | US$ 0,0257 | 1.560.363 | US$ 0,1745 |
| TRIASA CORRETORA DE SEGUROS | 73.168 | US$ 0,0281 | 446.182 | US$ 0,1544 | 937.138 | US$ 0,3693 |

Staff M&B e AVEP concentram **74,10% dos tokens e 81,01% do custo estimado** em 15 dias. A Staff lidera o volume; a AVEP lidera o custo em dólar. O piloto de redução deve começar nessas empresas, com critérios separados para frequência, tamanho de contexto e qualidade.

## Modelos e custo

| Modelo, 15 dias | Chamadas | Tokens | Parcela dos tokens | USD estimado | Parcela do custo |
| --- | --- | --- | --- | --- | --- |
| gpt-5.6-luna | 29.731 | 247.315.606 | 92,05% | US$ 56,6861 | 54,04% |
| gpt-5.6-terra | 685 | 20.683.938 | 7,70% | US$ 46,5008 | 44,33% |
| gpt-4o-mini-transcribe | 2.642 | 620.936 | 0,23% | US$ 1,4237 | 1,36% |
| gpt-4o-transcribe-diarize | 3 | 38.811 | 0,01% | US$ 0,2856 | 0,27% |
| gpt-4.1-mini | 4 | 11.556 | 0,00% | US$ 0,0040 | 0,00% |

Luna responde por 92,05% dos tokens e 54,04% do custo. Terra responde por 7,70% dos tokens e 44,33% do custo. As tarifas padrão de texto consultadas em 13/10 coincidem com as cadastradas para esses modelos: Luna US$ 0,20 / 0,02 / 1,20 e Terra US$ 2,00 / 0,20 / 12,00 por milhão de tokens de entrada / cache / saída. Fontes: [GPT-5.6 Luna](https://developers.openai.com/api/docs/models/gpt-5.6-luna) e [GPT-5.6 Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra).

Esta tabela usa as tarifas internas efetivas na data do evento. Não é reconciliação com fatura: não inclui consumo ausente nos registros, modalidades ou acréscimos que o esquema não mede. Os dois eventos de transcrição segmentada sem uso informado permanecem com custo desconhecido, embora a tarifa do modelo esteja cadastrada.

## Evolução diária

| Dia | Chamadas registradas | Tokens | USD estimado | Tentativas crm-ai | Falhas crm-ai |
| --- | --- | --- | --- | --- | --- |
| 2026-09-29 | 1.535 | 7.123.480 | US$ 5,4081 | 29.121 | 28.184 |
| 2026-09-30 | 3.539 | 25.708.252 | US$ 8,1299 | 35.419 | 32.219 |
| 2026-10-01 | 3.512 | 34.476.469 | US$ 13,7384 | 31.121 | 27.776 |
| 2026-10-02 | 3.085 | 29.560.209 | US$ 14,7906 | 13.575 | 10.800 |
| 2026-10-03 | 993 | 7.568.125 | US$ 2,9026 | 12.031 | 11.232 |
| 2026-10-04 | 523 | 4.248.352 | US$ 2,3405 | 11.899 | 11.425 |
| 2026-10-05 | 3.313 | 27.327.630 | US$ 14,8933 | 15.971 | 12.953 |
| 2026-10-06 | 4.797 | 40.378.876 | US$ 15,4771 | 16.276 | 11.973 |
| 2026-10-07 | 3.984 | 32.399.387 | US$ 8,9322 | 20.262 | 16.527 |
| 2026-10-08 | 3.818 | 28.452.127 | US$ 9,1519 | 34.600 | 30.933 |
| 2026-10-09 | 1.708 | 14.061.433 | US$ 4,4409 | 15.545 | 14.005 |
| 2026-10-10 | 0 | 0 | US$ 0,0000 | 26.272 | 26.272 |
| 2026-10-11 | 0 | 0 | US$ 0,0000 | 45.645 | 45.645 |
| 2026-10-12 | 0 | 0 | US$ 0,0000 | 52.363 | 52.363 |
| 2026-10-13 até 11h | 2.258 | 17.366.507 | US$ 4,6946 | 22.271 | 20.201 |

Os dias 10, 11 e 12/10 registram zero chamadas com consumo e zero sucessos nos logs, apesar de 26.272, 45.645 e 52.363 tentativas. Nesse conjunto, 86.123 falhas foram por saldo esgotado do provedor e 38.157 por chave local ausente. Isso exige investigar indisponibilidade por empresa/agente e interrupção das tentativas repetidas.

Comparando semanas completas, 29/09 a 05/10 registraram 136.012.517 tokens e US$ 62,2035. De 06 a 12/10 foram 115.291.823 e US$ 38,0021: queda de 15,23% em tokens e 38,91% em custo, contaminada pelos três dias sem sucessos. Hoje até 11h, comparado à terça-feira 06/10 até 11h, o volume aumentou 37,74% e o custo caiu 24,97%. Mudanças de demanda e mix de modelos impedem atribuir esse resultado a uma otimização específica.

## Falhas e disponibilidade

| Causa registrada | Hoje | 7 dias | 15 dias |
| --- | --- | --- | --- |
| missing-local-key | 6.098 | 81.208 | 222.757 |
| credit_balance_exhausted | 14.096 | 124.730 | 129.741 |
| unclassified | 7 | 8 | 9 |
| server_is_overloaded | 0 | 0 | 1 |

`missing-local-key` identifica a mensagem local de chave não configurada. `credit_balance_exhausted` é o código retornado pelo provedor; não foi confundido com a franquia comercial do CRM. Os 352.508 registros de falha não informaram tokens. Não foram atribuídos tokens fictícios a essas tentativas nem considerados valores economizados.

Multiplix concentra 219.139 falhas em 15 dias, 62,17% do total. Corrigir credenciais e disponibilidade de saldo resolve a causa; limitar novas tentativas enquanto o bloqueio persiste resolve a amplificação operacional. A orientação oficial é distinguir limitações temporárias de quota/cobrança e limitar tentativas, respeitando `Retry-After` ou backoff com jitter nos casos transitórios: [rate limits](https://developers.openai.com/api/docs/guides/rate-limits).

## Gargalo 1: verificação de sugestões

### Frequência

| Situação | Chamadas hoje | Tokens hoje | Chamadas 7 dias | Tokens 7 dias | Chamadas 15 dias | Tokens 15 dias |
| --- | --- | --- | --- | --- | --- | --- |
| Primeira chamada primária do dia por sugestão | 293 | 2.978.964 | 2.644 | 16.284.826 | 7.641 | 53.071.905 |
| Reavaliação primária no mesmo dia | 488 | 6.881.437 | 2.679 | 38.058.555 | 9.013 | 111.947.312 |
| Recuperação com contexto completo | 68 | 1.131.331 | 386 | 5.796.441 | 617 | 8.800.572 |

As categorias acima dividem todo o consumo do verificador, sem dupla contagem. “Primeira do dia” não é a primeira da vida da sugestão. Reavaliação não prova desperdício: podem existir novas interações e evidências.

As reavaliações consumiram **111.947.312 tokens em 15 dias**, equivalentes a **41,67% de todos os tokens do CRM**. Nos últimos 7 dias, foram 38.058.555 tokens, 41,24% do total. Hoje, foram 6.881.437, 39,62% do total.

Nos 7 dias, 870 chamadas primárias ocorreram a menos de 15 minutos da chamada primária anterior da mesma sugestão e 356 a menos de 5 minutos. Hoje foram 127 e 30. Esses grupos se sobrepõem e não medem chamadas automaticamente evitáveis.

Nos 15 dias houve 1.868 repetições de pedidos com `input` e `instructions` exatamente iguais dentro do mesmo dia, modelo, empresa e modo. As famílias com repetição acumularam 25.982.328 tokens, incluindo a primeira chamada. Nos últimos 7 dias foram 27 repetições e hoje nenhuma. Essa redução é observacional; não comprova a versão publicada nem atribui causalidade ao cache.

### Tamanho do contexto

| Evidências por chamada | Tokens hoje | Tokens 7 dias | Tokens 15 dias |
| --- | --- | --- | --- |
| 0-10 | 142.573 | 2.511.784 | 7.317.122 |
| 11-40 | 517.391 | 4.463.114 | 15.844.138 |
| 41-80 | 674.099 | 7.262.979 | 21.840.469 |
| 81-119 | 1.056.848 | 8.394.361 | 25.367.598 |
| 120 | 8.600.821 | 37.507.584 | 103.450.462 |

Contextos com mais de 80 evidências concentram **87,86% dos tokens do verificador hoje**, **76,33% nos 7 dias** e **74,11% nos 15 dias**. Hoje, 514 das 849 chamadas do verificador atingiram 120 evidências.

Nas chamadas compactas dos 7 dias, atividades automáticas `agent-skopos` ocupam **62,46% dos caracteres JSON das evidências**; hoje, 59,82%. São caracteres de representação, não atribuição exata dos tokens do pedido. A mesma atividade pode reaparecer em muitas chamadas. Esse recorte se sobrepõe ao de frequência e não pode ser somado a ele como economia adicional.

O código coleta até 120 evidências com resumos de até 1.200 caracteres, a partir de 24h antes da criação da sugestão. Atividades automáticas já deixaram de ser gatilho na implementação local, mas continuam no contexto; o seletor existente conserva registros estruturados. A solução é uma representação menor que preserve ação, responsável, data, estado e prova, com seleção por relevância e tamanho.

### Recuperações e resultados

Hoje ocorreram 68 recuperações completas, com 1.131.331 tokens adicionais. Nos 7 dias foram 386 recuperações, com 5.796.441 tokens. Isso equivale a 8,84% das chamadas compactas hoje e 8,34% nos 7 dias.

Foi possível associar todas as recuperações à chamada compacta anterior da mesma sugestão, empresa e modelo dentro de dois minutos. A associação é temporal, sem identificador transacional explícito. **37 das 68 duplas hoje e 197 das 386 duplas nos 7 dias mudaram o resultado**; isso não demonstra qual resultado estava correto. As duas passagens dos pares consumiram 2.094.953 tokens hoje e 10.778.523 nos 7 dias. Esse total inclui a primeira passagem e não deve ser somado ao consumo do verificador.

Preservar a recuperação quando necessária. Avaliar entrada completa direta nos perfis em que a economia da compactação é pequena e a recuperação é frequente. Não aceitar verificação sem evidência apenas para reduzir custo.

Nos 7 dias, 4.735 respostas de chamada foram `unfulfilled`, 632 `inconclusive` e 342 `fulfilled`. Esses resultados incluem tentativas compactas e recuperações, não correspondem a sugestões únicas concluídas. Hoje foram 703, 103 e 43. O custo por sugestão realmente concluída exige correlacionar o resultado final ao fluxo completo.

### O que já existe e o que falta verificar

O código local já possui gate por interação nova, execução determinística sem evidência posterior e cache por conteúdo/configuração/política. Risco já possui debounce por empresa e descarte de trabalhos obsoletos. O estado local do código não confirma implantação.

O scheduler do verificador ainda agenda imediatamente e não aplica `debounce_minutes`. Novos eventos podem também antecipar uma nova tentativa após falha. Verificar a versão em produção e medir acertos do gate/cache antes de tratar essas funcionalidades como ausentes.

## Gargalo 2: contexto duplicado e fixo no WhatsApp

| Período | Chamadas | Com contexto duplicado | Caracteres repetidos | Scorecard / input JSON | Novo trecho / input JSON |
| --- | --- | --- | --- | --- | --- |
| Hoje | 456 | 456 | 546.273 | 55,10% | 4,16% |
| 7 dias | 2.782 | 2.782 | 5.233.401 | 48,92% | 3,78% |
| 15 dias | 6.263 | 6.263 | 8.824.531 | 37,57% | 4,50% |

Em todas as 6.263 chamadas de análise de WhatsApp, `additionalContext` também apareceu integralmente em `instructions`: **8.824.531 caracteres repetidos**. Preservar uma cópia é uma correção delimitada. A economia exata em tokens e dólares precisa ser medida.

Hoje, o scorecard ocupa 55,10% dos caracteres do input JSON; sugestões existentes, 14,82%; novo trecho da conversa, 4,16%. Isso indica contexto fixo grande em relação à novidade. Compactar scorecard e deduplicação mantendo critérios, estado incremental e identificação das sugestões existentes.

O caminho equivalente também existe no código do Instagram, mas esta auditoria não quantificou sua duplicação. O volume do WhatsApp justifica começar por ele.

## Estoque de pendências

No snapshot consultado havia **4.732 sugestões pendentes**, **2.896 com mais de 7 dias** e **1.766 com agendamento elegível até o corte**. Esse é o estado observado no momento da consulta, não uma reconstrução histórica das 11h.

Alguns grupos acumulam centenas de tentativas médias ao longo da vida. Esse contador pode incluir falhas e execuções determinísticas, portanto não equivale a chamadas faturadas. Revisar relevância e frequência das pendências antigas conforme a regra comercial, preservando a prova de cumprimento e o tratamento de respostas novas.

## Plano de ação proposto

| Prioridade | Ação concreta | Onde começar | Como validar |
| --- | --- | --- | --- |
| 0 | Corrigir chave/saldo e impedir repetição contínua da mesma falha bloqueante. Retomar após recuperação confirmada. | Multiplix e grupos sem sucesso; providers/worker/scheduler | Recuperação de execuções, queda das tentativas pela mesma causa e ausência de perda de eventos. Separar quota de limitação transitória. |
| 1 | Confirmar versão publicada e instrumentar gate, cache, chamadas determinísticas e eventos agrupados. | Verificador | Contagem de chamadas evitadas por motivo, com mesmo perfil de demanda e preservação das conclusões. |
| 1 | Reduzir texto automático e selecionar evidências por relevância e orçamento de contexto. | Verificador da Staff e AVEP | Tokens por execução completa, incluindo recuperação, com acerto de cumprimento e cobertura de evidência. |
| 1 | Agrupar interações próximas com janela por empresa, adiamento máximo e tratamento próprio de prazos/respostas. | Staff e AVEP; piloto inicial de 5 minutos a calibrar | Chamadas por sugestão, atraso até verificação, quantidade de alertas corretos e custo por resultado final. |
| 1 | Remover cópia exatamente repetida de `additionalContext`. | WhatsApp; depois medir Instagram | Uma cópia preservada, redução de entrada/custo e qualidade equivalente. |
| 2 | Compactar scorecard e sugestões existentes sem retirar critérios ou estado. | Análise de WhatsApp | Avaliação de scorecard, deduplicação, resumo e sugestões, além dos tokens. |
| 2 | Escolher representação inicial considerando ganho e probabilidade de recuperação. | Verificador | Custo das duas passagens, taxa de recuperação e resultado final fundamentado. |
| 2 | Reduzir contexto de risco e comparar configurações/modelos em amostra controlada. | AVEP e Staff | Acerto e cobertura de riscos; tokens e USD por análise aceita. Preservar debounce já existente. |
| 2 | Corrigir lacunas da telemetria: persistência independente de débito e cache nos dois formatos de usage. | Consumers e camada de medição | Reconciliação com uso do provedor, registro de falhas com uso e sinal explícito para uso ausente. |

O piloto de 5 minutos é uma proposta, não configuração alterada nem meta universal. Os agentes e empresas precisam de limites compatíveis com a urgência do atendimento.

O guia oficial recomenda reduzir entrada e quantidade de pedidos onde apropriado: [latency optimization](https://developers.openai.com/api/docs/guides/latency-optimization). Para contexto reutilizável, avaliar a configuração de cache suportada pelo modelo e manter material estável com isolamento por empresa/permissão: [prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching). Cache do provedor e cache de resultado da aplicação são mecanismos distintos.

Não há percentual de economia comprovado para essas propostas. Como cenário aritmético, reduzir proporcionalmente 20% das chamadas e do custo do verificador, mantendo o mesmo mix, corresponderia a 34.763.958 tokens e US$ 7,5508 no recorte de 15 dias. Reduzir 20% do custo de risco corresponderia a US$ 6,7223. Esses cenários não são previsão mensal e não devem ser somados a cortes de contexto que atinjam as mesmas chamadas. A indisponibilidade dos dias 10 a 12 limita qualquer extrapolação.

## Método, reconciliação e limites

- Janelas de calendário: hoje [13/10 00h, 13/10 11h), 7 dias [07/10 00h, 13/10 11h) e 15 dias [29/09 00h, 13/10 11h), America/Sao_Paulo. Limite inicial inclusivo e final exclusivo.
- Fonte principal: `ai_usage_events` de backend, consumers e crm-ai. Log complementar: `ai_agent_invocation_logs`, usado para diagnóstico e medição de chamadas que eventualmente não tenham evento.
- Deduplicação por empresa e `invocation_id` sem hífens correspondente ao UUID do log. Evento usa `created_at`; chamada sem evento usa `started_at`. No corte houve **29.863 eventos ligados a logs**, **3.202 eventos sem log** e **zero logs com uso sem evento**. Não houve divergência de entrada/saída/cache nem diferença de dia nos registros associados.
- As somas por dia, agente e empresa reconciliam os **268.670.847 tokens**. As classificações de reavaliação e tamanho de evidência reconciliam separadamente os **173.819.789 tokens do verificador**.
- As estatísticas de falha cobrem apenas o crm-ai. Não há base neste diagnóstico para atribuir a mesma taxa aos consumers, à API inteira ou à experiência final de todos os usuários.
- O cálculo monetário aplica a tarifa cadastrada efetiva no instante do evento, priorizando modelo exato sobre `*`: `((entrada - cache) × tarifa_entrada + cache × tarifa_cache + saída × tarifa_saída) / 1.000.000`. O painel atual usa a tarifa vigente, que poderia gerar resultado diferente se houver mudança de tarifa. Não havia chamada com tarifa ausente neste recorte.
- Houve **duas transcrições segmentadas bem-sucedidas sem tokens informados**, Staff M&B, 30/09 e 01/10. O custo integral dessas chamadas permanece desconhecido; zero registrado não significa consumo real zero.
- Foram calculados **310.737 créditos comerciais**, mas **zero créditos debitados**: todos os eventos estavam em `shadow` (251.575 calculados) ou `unlimited` (59.162). O livro de créditos não registrou lançamentos `consume` no recorte. Isso não elimina a despesa do provedor.
- As falhas não registraram usage. Logs e eventos podem perder chamadas por falha de persistência; nos consumers, uma chamada concluída pode ficar sem evento após rollback por falta de créditos. Não se alegou integralidade da fatura.
- Requests foram inspecionados no banco apenas para contagens, hashes e tamanhos. Não foram exportadas mensagens, transcrições, prompts, credenciais ou evidências de clientes.
- Todas as consultas usaram transações `READ ONLY`, isolamento `REPEATABLE READ`, timeout por statement e `ROLLBACK`. Corte de consumo fixo em 11h; snapshots de configuração/pendências refletem o momento das consultas. A conferência final encontrou zero outras conexões de auditoria, e o cliente final encerrou sua conexão.
- Nenhuma configuração, serviço, fila, agente ou dado de negócio de produção foi alterado. Esta entrega contém diagnóstico e propostas.

## Evidências e reprodução

[Agregação de consumo e falhas](aggregate.sql), [diagnósticos de frequência e payload](diagnostics.sql), [composição das evidências e recuperações](details.sql), [tarifas e integridade inicial](probe.sql). As consultas não contêm credenciais. Os arquivos JSONL adjacentes contêm somente agregados/metadados minimizados.

Referências de código local para as soluções:

- `crm-ai/src/CrmAi.Infrastructure/Persistence/SuggestionCompletionVerificationHostedService.cs`: cache/determinístico antes de chamada, coleta de evidência, até 120 itens e retomada após falha.
- `crm-ai/src/CrmAi.Infrastructure/Persistence/SuggestionCompletionVerificationScheduler.cs:27`: agendamento imediato sem debounce da configuração.
- `crm-ai/src/CrmAi.Infrastructure/Persistence/SuggestionVerificationActivityGate.cs`: gate por interação nova.
- `crm-ai/src/CrmAi.Infrastructure/Persistence/SuggestionVerificationCache.cs`: versão interaction-driven e exclusão de contexto sintético.
- `crm-ai/src/CrmAi.Application/SuggestionEvidenceSelection.cs:83`: preservação dos registros estruturados.
- `crm-ai/src/CrmAi.Application/AiAgentRuntimeSettings.cs:15` e `crm-ai/src/CrmAi.Infrastructure/Persistence/PostgresWhatsappConversationAnalysisScheduler.cs:106`: caminho da duplicação de contexto.
- `crm-ai/src/CrmAi.Application/SuggestionCompletionVerificationModels.cs`: compactação e critérios de recuperação.
- `crm-ai/src/CrmAi.Application/RiskAnalysisScheduling.cs`: debounce já existente em risco.
- `crm-ai/src/CrmAi.Infrastructure/Persistence/PostgresAiAgentInvocationLogStore.cs`: persistência e medição.
- `CONSUMERS_CRM/src/CrmRestApi.Consumers.Worker/Consumers/AiCreditMeter.cs`: uso, reserva e débito.
- `BACKEND_CRM_SAAS/src/CrmRestApi.Infrastructure/ApplicationServices/SaasService.cs:431`: agregação do painel e tarifa vigente.

Histórico comparativo: [situações de consumo auditadas em 08/10](../skopos-token-situations-2026-10-08.md) e [custos dos alertas em 08/10](../skopos-alert-costs-2026-10-08.md). Os números históricos não foram usados como medição de hoje.
