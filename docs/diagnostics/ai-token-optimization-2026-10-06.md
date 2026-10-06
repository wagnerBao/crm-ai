# Consumo de tokens da IA em produção em 6 de outubro de 2026

A compactação do verificador de sugestões está em uso e reduz a entrada das chamadas. A economia líquida por execução ainda é pequena na janela observada: **0,20%**, incluindo saída e chamadas adicionais de recuperação. O consumo total do verificador aumentou **90,68%**, acompanhando um aumento de **91,06%** no número de execuções primárias. Portanto, os dados ainda não demonstram uma redução relevante no consumo total de produção.

## Período e método de comparação

Consultas realizadas em 06/10/2026, aproximadamente entre 13h18 e 13h23, no horário de São Paulo, ao PostgreSQL que recebe invocações atuais. A connection string foi obtida do arquivo de implantação de webhooks; o banco indicado no arquivo local do backend não tinha invocações nos últimos 14 dias. Credenciais e conteúdo de clientes foram omitidos deste diagnóstico.

As janelas fixas foram **05/10/2026, das 10h20 às 13h19**, e **06/10/2026, das 10h20 às 13h19**, ambas com 179 minutos. Os limites finais são exclusivos. A primeira marca da nova compactação ocorreu hoje às **10h10min57s**; a primeira chamada compacta, às **10h11min12s**. Chamadas da versão anterior ainda aparecem até 10h11min21s. A janela posterior começa às 10h20 para evitar misturar essa transição.

A fonte é `ai_agent_invocation_logs`, com os tokens reportados pelo provedor. Todas as chamadas com uso registrado são contabilizadas, incluindo recuperação completa. Registros sem uso são apresentados como falhas operacionais, sem atribuir tokens fictícios. Não houve sucesso sem tokens de entrada na janela posterior.

Execução primária significa uma chamada com uso registrado cujo modo não é `full-fallback`; a chamada de recuperação acrescenta tokens à execução, sem aumentar esse denominador. Esse indicador mede apenas as execuções que chegaram ao provedor e tiveram consumo registrado; não mede todos os eventos recebidos, acertos de cache, verificações determinísticas nem sugestões únicas.

As cargas não são idênticas. Empresas, sugestões e tamanho das evidências variam entre dias. A comparação por execução melhora a leitura do volume, mas não estabelece causalidade, economia futura nem equivalência de qualidade. A verificação cobre cerca de três horas após a entrada da versão nova.

## Verificador de sugestões

| Indicador | Antes | Depois | Variação |
| --- | ---: | ---: | ---: |
| Execuções primárias com uso registrado | 414 | 791 | +91,06% |
| Chamadas com uso registrado | 414 | 867 | +109,42% |
| Chamadas adicionais de recuperação | 0 | 76 | 76 adicionais |
| Tokens de entrada por chamada | 10.208,75 | 9.316,35 | −8,74% |
| Tokens totais por execução primária | 10.462,35 | 10.441,49 | −0,20% |
| Tokens de entrada na janela | 4.226.422 | 8.077.274 | +91,11% |
| Tokens de saída na janela | 104.989 | 181.941 | +73,30% |
| Tokens totais na janela | 4.331.411 | 8.259.215 | +90,68% |

Na janela posterior, houve **723 chamadas compactas**, **68 chamadas no formato original** e **76 recuperações completas**, todas com uso registrado. A recuperação ocorreu em **10,51%** das chamadas compactas e consumiu **1.044.535 tokens**, ou **12,65%** dos tokens do verificador nessa janela. Essa segunda passagem é o principal custo adicional observado no novo caminho; a média por chamada isolada não mostra seu efeito sobre o custo da execução.

Os metadados das 723 chamadas compactas somam 23.560.987 caracteres antes e 21.400.912 depois, incluindo as instruções adicionais: redução de **9,17% no tamanho textual**. Caracteres não são tokens faturados.

A consulta associou as 76 recuperações à chamada compacta anterior da mesma empresa, modelo e sugestão, em até dois minutos. Todas tiveram IDs compactos distintos e o mesmo tamanho original de contexto nos metadados. Nessas duplas, a entrada completa somou **1.022.856 tokens**, contra **865.717 tokens** na entrada compacta: redução observada de **15,36%** na passagem compacta. Essa amostra é formada pelos casos que precisaram de recuperação e não representa todas as chamadas. Além disso, as instruções das duas passagens diferem.

As duas passagens desses casos consumiram juntas **1.928.236 tokens de entrada e saída**. Em 37 duplas o status final mudou entre a resposta compacta e a completa. Isso confirma que remover indiscriminadamente a recuperação alteraria resultados; concordância ou divergência de status não comprova correção sem revisão das evidências.

## Cache e outras otimizações

O código reutiliza verificações `unfulfilled` ou `inconclusive` quando o fingerprint do contexto e da política permanece igual. A reutilização atualiza a sugestão, sem inserir uma nova linha no histórico de verificação nem chamar o provedor.

O estado persistido às 13h23 apresentou **211 sugestões** com fingerprint igual ao último histórico, mas `last_verified_at` avançado em mais de um minuto sem novo histórico: **198 unfulfilled**, reagendadas em 15 minutos, e **13 inconclusive**, reagendadas em 60 minutos. Esses registros são compatíveis com o caminho de reuso. São sugestões observadas no estado atual, não a contagem histórica de acertos nem uma medição dos tokens evitados. Sem contadores históricos de cache, não é possível atribuir uma porcentagem de economia ao reuso.

As **16 análises de risco** da nova versão usaram `risk-meeting-context-v1` com `contextMode=original`. Não houve chamada compacta, de comparação ou recuperação para risco no período. A compactação de reuniões não gerou economia observada; seu uso depende do modo, da inclusão da empresa e de resumos elegíveis. Os registros de execução não permitem distinguir sozinhos qual dessas condições impediu o uso.

Não houve chamada com `evidenceSelectionVersion` registrada entre 10h10 e 13h19. Assim, não há consumo ou economia observados da seleção de evidências. Não ocorreu execução de checkout nas janelas comparadas; sua compactação ainda precisa ser medida em uma execução posterior à implantação.

## Consumo dos demais agentes

| Agente | Tokens totais antes | Tokens totais depois | Chamadas com uso antes e depois |
| --- | ---: | ---: | ---: |
| Verificador de sugestões | 4.331.411 | 8.259.215 | 414 → 867 |
| WhatsApp | 2.339.341 | 2.830.867 | 283 → 291 |
| Instagram | 615.247 | 551.844 | 259 → 200 |
| Risco | 761.152 | 666.891 | 16 → 16 |
| Total dos agentes com consumo na janela | 8.047.151 | 12.308.817 | 972 → 1.374 |

O total registrado desses agentes aumentou **52,96%**. As quedas de Instagram e risco não demonstram efeito das otimizações: Instagram teve menos chamadas, enquanto risco usou o formato original e uma amostra pequena. WhatsApp variou em volume, modelo e contexto. Tokens de entrada em cache já estão incluídos nos tokens de entrada; não devem ser somados novamente. Este diagnóstico não concilia cobrança do fornecedor nem estima valores monetários.

## Falhas que afetam a comparação

Na janela posterior houve **173 respostas HTTP 429**: 78 de Instagram, 52 do verificador e 43 de WhatsApp. O corpo estruturado informa `credit_balance_exhausted` e `insufficient_quota`. São falhas por saldo ou quota do provedor, sem uso registrado, e não evidência de economia. Esse impedimento limita a carga que efetivamente foi processada.

Também houve **1.509 erros locais de chave ausente**: 1.463 do verificador e 46 de Instagram. Esses erros não enviaram uma chamada autenticada com uso registrado. Não foram classificados como tokens economizados pela nova versão.

## Próximas medições

1. Medir 24 a 48 horas com tokens totais por execução e por empresa, incluindo recuperações, além do número de eventos recebidos e execuções concluídas. O checkout precisa de sua primeira amostra após a implantação.
2. Registrar acertos de cache e execuções determinísticas para separar chamadas evitadas de falhas por chave ou saldo. O snapshot atual confirma sinais de reuso, mas não mede o volume histórico.
3. Revisar os casos de recuperação para reduzir sua frequência preservando evidências e qualidade. A amostra atual teve mudança de status em 37 das 76 duplas.
4. Avaliar os modos de reuniões e seleção de evidências em um piloto com empresas delimitadas e revisão dos resultados, antes de habilitar a redução. O modo de comparação executa chamadas extras e precisa ser contabilizado separadamente.

## Consultas e escopo operacional

[Consultas de reprodução](ai-token-optimization-2026-10-06.sql) usam transação de leitura, timeout de 30 segundos e `ROLLBACK`. `transaction_read_only=on` foi confirmado. As conexões de consulta foram encerradas após cada comando. As consultas de cache refletem o estado ao serem executadas; não recriam um snapshot histórico.

A execução das novas versões foi confirmada pelos metadados dos logs. O acesso SSH com a chave local não autenticou, portanto a imagem exata e a configuração atual do Swarm não foram inspecionadas. Nenhum dado, configuração, fila ou serviço de produção foi alterado. Somente este diagnóstico e suas consultas foram acrescentados ao repositório, sem commit ou implantação.
