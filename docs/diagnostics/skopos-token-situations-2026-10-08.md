# Situações que mais consomem tokens no Agent Skopos

Relatório de 08/10/2026, com consultas entre aproximadamente 14h44 e 14h48, horário de São Paulo. A referência principal é **07/10/2026, das 00h00 às 00h00 de 08/10**, um dia completo. Foram registrados **32.247.961 tokens** nos agentes presentes em `ai_agent_invocation_logs`.

**A maior concentração está nas reavaliações da mesma sugestão: 15,43 milhões de tokens, 47,84% do total.** Dentro do verificador, os contextos com mais de 80 evidências concentram 74,00% do consumo. Há também uma duplicação comprovada do contexto adicional nas chamadas de WhatsApp.

## Ranking por situação — sem dupla contagem

As três linhas de verificação abaixo dividem todo o consumo do verificador. As demais linhas são outros agentes. Cada chamada aparece uma única vez nesta tabela.

| Situação | Chamadas com uso | Tokens totais | Participação no dia |
| --- | ---: | ---: | ---: |
| Reavaliar a mesma sugestão, depois da primeira verificação do dia | 1.155 | 15.426.286 | **47,84%** |
| Analisar conversas de WhatsApp | 1.019 | 7.700.152 | **23,88%** |
| Primeira verificação registrada no dia para cada sugestão | 964 | 5.278.762 | 16,37% |
| Segunda chamada com contexto completo após uma tentativa compacta | 142 | 2.130.836 | 6,61% |
| Analisar conversas de Instagram | 410 | 1.170.447 | 3,63% |
| Analisar risco de oportunidades | 35 | 324.535 | 1,01% |
| Gerar checkout diário | 5 | 131.200 | 0,41% |
| Gerar Skopos Coach | 5 | 85.743 | 0,27% |
| **Total** | **3.735** | **32.247.961** | **100%** |

“Primeira do dia” não significa a primeira verificação da vida da sugestão. “Reavaliação” não significa duplicação idêntica ou processamento desnecessário: as evidências podem ter mudado. Os logs não registram o motivo exato de cada agendamento; não é possível separar com precisão, só por eles, disparo por nova análise, prazo vencido e verificação periódica.

## 1. Reavaliar sugestões pendentes várias vezes

As reavaliações representam **67,55% dos tokens do verificador**, excluindo suas chamadas adicionais de recuperação. A primeira verificação do dia representa 23,12%, e a recuperação, 9,33%.

| Empresa | Tokens do verificador | Tokens das reavaliações no dia | Reavaliações / sugestões que foram reavaliadas | Parcela do consumo da empresa no verificador |
| --- | ---: | ---: | ---: | ---: |
| Staff M&B | 13.519.431 | **9.936.134** | 679 / 169 | **73,50%** |
| AVEP | 5.171.822 | **3.033.633** | 172 / 45 | **58,66%** |
| Wagner Brum | 2.956.218 | **2.322.933** | 272 / 31 | **78,58%** |
| Lumyne Estética Avançada | 317.578 | 125.684 | 30 / 14 | 39,58% |
| Vetor Modular | 801.911 | 7.902 | 2 / 2 | 0,99% |
| TRIASA | 68.924 | 0 | 0 / 0 | 0% |

Exemplo concreto: uma única sugestão da Staff recebeu **21 chamadas no dia**, sem recuperação, e acumulou **594.215 tokens**. Outra teve 17 chamadas e 482.262 tokens. Esses números mostram que o problema não se limita à segunda passagem da compactação.

As dez sugestões com maior consumo acumularam **3.921.092 tokens**, **17,17%** do consumo do verificador.

**Onde atuar:** agrupar eventos próximos e verificar a evidência mais recente uma vez por janela; revisar sugestões antigas ainda pendentes. Preservar a identificação de evidências novas e a publicação de alertas de resposta. Não considerar automaticamente uma sugestão cumprida para economizar.

## 2. Verificar sugestões com contexto grande

Os dados abaixo são outro recorte do mesmo verificador e **não devem ser somados** ao ranking anterior. Incluem as recuperações.

| Evidências enviadas por chamada | Chamadas | Tokens totais | Parcela dos tokens do verificador |
| --- | ---: | ---: | ---: |
| Até 10 | 617 | 1.053.839 | 4,61% |
| 11–40 | 351 | 1.697.061 | 7,43% |
| 41–80 | 234 | 3.186.121 | 13,95% |
| 81–119 | 253 | **4.827.840** | **21,14%** |
| 120, limite atual da coleta | 806 | **12.071.023** | **52,86%** |

Nas chamadas compactas, a média foi de aproximadamente **2.073 tokens** para até dez evidências, **4.730** para 11–40, **13.409** para 41–80, **19.083** para 81–119 e **14.714** no limite de 120. O número de registros sozinho não determina tamanho: textos de atividades podem ser muito maiores que mensagens curtas. Por isso, 120 registros não tiveram a maior média.

**O que ocupa o contexto:** nas evidências compactas, atividades representam **61,35% dos caracteres**, e mensagens de WhatsApp, **37,42%**. Dentro das atividades, **97,86% dos caracteres pertencem a registros do tipo `agent-skopos`**. Essas atividades automáticas, portanto, representam cerca de **60,04% de todos os caracteres das evidências compactas**.

São 25.376 ocorrências de atividades automáticas nas chamadas compactas; a mesma atividade pode aparecer em diversas chamadas. Isso não significa 25.376 atividades únicas criadas no dia.

**Onde atuar:** revisar como os registros automáticos do Agent retornam ao verificador. Evitar repetir textos já representados e usar uma representação mais enxuta que preserve ação, responsável, data, estado e prova de execução. Remover todas essas atividades indiscriminadamente pode eliminar fatos relevantes.

Essas participações são de **caracteres da representação JSON das evidências**, não uma atribuição exata de tokens por campo. Elas não incluem todas as instruções, o esquema de resposta ou todo o restante do pedido. O provedor mede tokens do pedido inteiro.

## 3. Analisar WhatsApp enviando muito mais contexto que mensagens novas

O WhatsApp consumiu **7.700.152 tokens** em 1.019 chamadas, média de **7.556,58 tokens por chamada**.

Participação dos componentes nos caracteres dos valores JSON do `input`, sem incluir as instruções externas:

| Componente | Caracteres acumulados | Participação |
| --- | ---: | ---: |
| Template e estado do scorecard | **5.639.402** | **54,60%** |
| Contexto adicional | **1.866.390** | **18,07%** |
| Sugestões existentes para deduplicação | **1.164.631** | **11,28%** |
| Trecho novo da conversa | **389.049** | **3,77%** |
| Outros componentes | 1.268.861 | 12,29% |

O scorecard é parte da mesma chamada que gera resumo, sugestões e avaliação. A concentração de tamanho não justifica desativar essa funcionalidade; indica oportunidade de reduzir a representação mantendo seus critérios e seu estado incremental.

### Duplicação comprovada de instruções adicionais

Nas **1.019 chamadas**, o texto de `additionalContext` também apareceu integralmente dentro de `instructions`. Foram **1.828.488 caracteres de texto repetido** nos pedidos, antes de considerar escapes e representação JSON.

| Empresa | Chamadas com duplicação | Caracteres de texto adicional repetido |
| --- | ---: | ---: |
| Staff M&B | 523 | **1.496.303** |
| AVEP | 168 | 254.184 |
| Wagner Brum | 14 | 40.054 |
| Lumyne | 301 | 34.314 |
| Vetor Modular | 1 | 2.265 |
| TRIASA | 12 | 1.368 |

**Onde atuar:** enviar uma única cópia dessas instruções quando os textos forem exatamente iguais. É uma melhoria delimitada, com conteúdo preservado. A economia de tokens precisa ser medida; caracteres, tokenização e desconto de cache são diferentes.

### Chamadas médias por empresa

| Empresa | Tokens médios de entrada | Tokens médios de saída | Caracteres médios das instruções | Caracteres médios do novo trecho no JSON |
| --- | ---: | ---: | ---: | ---: |
| AVEP | 9.668 | 1.342 | 13.373 | 498 |
| Wagner Brum | 9.655 | 1.516 | 12.092 | 1.019 |
| Staff M&B | 6.420 | 1.065 | 12.181 | 308 |
| Lumyne | 4.320 | 1.322 | 8.449 | 410 |

As instruções e os componentes fixos ajudam a explicar por que mensagens novas curtas ainda geram pedidos grandes. Essas médias não atribuem todos os tokens a um componente específico.

## 4. Recuperação após resultado compacto insuficiente

As **142 chamadas adicionais completas** consumiram **2.130.836 tokens**, média de **15.006 tokens por recuperação**. As duas passagens desses casos, somadas, consumiram **3.952.930 tokens**.

O consumo é adicional ao da primeira passagem. Entre as duplas previamente auditadas, **74 mudaram o resultado**. Remover a recuperação ou aceitar respostas sem prova para reduzir tokens pode comprometer a verificação.

**Onde atuar:** avaliar o envio direto da representação completa quando a compactação economiza pouco ou costuma exigir recuperação. Preservar a segunda passagem nos casos em que ela é necessária e a validação de evidências. A economia líquida depende de um piloto medido.

## 5. Sugestões antigas e respostas que continuam indicando pendência

| Idade da sugestão no momento da chamada | Tokens do verificador | Participação |
| --- | ---: | ---: |
| Menos de um dia | 6.102.688 | 26,72% |
| 1–7 dias | **8.109.479** | **35,51%** |
| 7–30 dias | **6.381.260** | **27,94%** |
| 30 dias ou mais | 2.242.457 | 9,82% |

As chamadas relativas a sugestões com sete dias ou mais consumiram **8.623.717 tokens**, **37,76%** do verificador. Não são um grupo adicional ao ranking; incluem primeiras verificações do dia, reavaliações e recuperações.

As respostas de chamada classificadas como `unfulfilled` somaram **19.235.787 tokens**, **84,23%** dos tokens do verificador. Esse é o resultado de cada chamada, incluindo tentativas compactas, e não uma contagem de sugestões únicas nem necessariamente o status final após recuperação.

**Onde atuar:** revisar o estoque de pendências e sua frequência de acompanhamento. O cache pode evitar uma chamada quando a evidência permanece igual; atualizações reais de contexto precisam continuar sendo avaliadas.

## 6. Situações com muitos tokens por chamada, mas baixo volume no dia

- **Checkout diário:** média de **26.240 tokens** nas cinco chamadas, máxima de **77.458**. É o maior consumo médio por chamada neste dia, mas representa só **0,41%** do total.
- **Skopos Coach:** média de **17.149 tokens**, cinco chamadas, **0,27%** do total.
- **Risco:** média de **9.272 tokens**, máxima de **40.627**, **1,01%** do total.

O maior pedido individual não é necessariamente a prioridade de redução. As reavaliações têm volume suficiente para dominar o consumo acumulado.

## Prioridades propostas

1. **Reduzir a repetição de contexto no verificador**, começando pelas atividades automáticas grandes da Staff e AVEP, preservando os fatos necessários para reconhecer cumprimento.
2. **Agrupar reavaliações próximas**, principalmente na Staff e Wagner. O campo de intervalo do verificador ainda não controla o scheduler; mudar somente a configuração não implementa esse agrupamento.
3. **Eliminar a duplicação exata de `additionalContext` no WhatsApp**, preservando uma cópia nas instruções.
4. **Compactar a representação do scorecard e de sugestões existentes**, mantendo todos os critérios, estado e deduplicação.
5. **Reduzir recuperações por uma escolha melhor da representação inicial**, com validação de qualidade; não simplesmente desabilitá-las.

O relatório não quantifica esses grupos como tokens automaticamente evitáveis. São concentrações observadas e oportunidades a validar. Os efeitos de cada mudança devem ser medidos por sugestão e por conversa, incluindo recuperações.

## Método, validação e escopo

- Fonte: tokens reportados pelo provedor e salvos em `ai_agent_invocation_logs`; os recortes principais usam somente chamadas com `prompt_tokens` registrado, incluindo chamadas adicionais.
- Entrada representa **94,34%** dos tokens dos agentes no dia e **97,93%** do verificador. Entrada em cache está incluída na entrada total e não foi somada novamente.
- Token total não equivale a custo em dólar: modelos e entrada em cache têm tarifas diferentes. Este relatório mede tokens; a estimativa monetária está no [diagnóstico de custos](skopos-alert-costs-2026-10-08.md).
- Falhas locais de chave e erros sem uso informado não foram tratados como tokens consumidos ou economizados. O saldo esgotado observado em produção limita o volume processado; não há previsão de demanda plena a partir desta amostra.
- Os agentes que registram somente `ai_usage_events`, como certas transcrições/atendimentos, não estão incluídos neste total de logs de invocação.
- As somas por agente/empresa reconciliam os 32.247.961 tokens. Reavaliações, quantidade de evidências, idade e resultados reconciliam separadamente os 22.835.884 tokens do verificador.
- Tamanhos por componente usam caracteres da representação JSON no PostgreSQL; referências compactas de resumo foram resolvidas somente para identificar o tipo de atividade. Nenhum texto de cliente foi exportado.
- [SQL reproduzível](skopos-token-situations-2026-10-08.sql): transações somente de leitura, timeout de 30 segundos, encerradas com `ROLLBACK` e saída do cliente ao final de cada execução.
- Somente documentação e SQL de auditoria foram acrescentados localmente. Nenhum código de execução, empresa, serviço ou dado de negócio foi alterado. Sem commit ou implantação.
- A conferência final confirmou `transaction_read_only=on` e zero outras sessões `psql` no banco. O cliente dessa conferência também encerrou sua conexão após `ROLLBACK`.
