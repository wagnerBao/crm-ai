# Análise de Jev e System One para o CRM Skopos

Análise realizada em 5 de outubro de 2026, por volta de 12h45 a 12h52, no horário de São Paulo. Foram examinados os cinco repositórios do CRM, a estrutura do website, os serviços em produção, amostras dos logs e consultas agregadas no PostgreSQL.

A recomendação é experimentar Jev como um avaliador de decisões delimitadas, começando pela verificação de cumprimento de sugestões. Os modelos atuais continuam necessários para escrever respostas, resumos e recomendações. A integração ainda precisa de acesso ao fornecedor e de uma avaliação com exemplos reais em português. Nenhum código de execução, configuração de produção ou registro de negócio foi alterado nesta análise.

## O que a tecnologia oferece

Jev recebe um estado textual ou estruturado e responde perguntas previamente definidas. `Choice` seleciona uma opção; `Score` avalia níveis de uma rubrica; `Noul` retorna a probabilidade de uma afirmação ser verdadeira. Perguntas independentes podem compartilhar uma chamada. Isso favorece componentes pequenos cuja saída o código consegue validar e usar. [Introdução](https://docs.typesafe.ai/introduction).

A publicação anuncia chamadas de 70 a 500 ms e ganhos expressivos de custo e velocidade. São resultados do fornecedor, não medições do CRM. Os benchmarks usam consenso de outros modelos como referência; não demonstram, por si só, a correção das decisões comerciais. A afirmação de ausência de alucinação deve ser entendida no contexto da garantia de formato e opções permitidas: uma opção válida ainda pode ser semanticamente errada. [Publicação](https://typesafe.ai/blog/introducing-system-one-models-and-jev), [avaliações](https://evals.typesafe.ai/).

A documentação atual informa `jev-1.13.0`, US$ 0,042 por milhão de tokens de entrada e saída gratuita. Aceita somente texto, com limite de 64 mil tokens por requisição e 32 mil para estado mais a maior pergunta. Inglês é o idioma de melhor desempenho. Esses limites exigem filtragem de contexto e avaliação específica em português. [Modelos](https://docs.typesafe.ai/models).

## Evidências de produção

Os serviços Docker estavam com as réplicas previstas: IA 3/3, consumers 2/2, API 1/1, frontend 1/1, webhooks 1/1 e website 1/1. PostgreSQL estava marcado como saudável; `systemctl --failed` não apresentou unidades. Isso confirma disponibilidade dos processos, sem provar o funcionamento integral de cada fluxo.

As consultas usaram o host, porta, banco e usuário fornecidos. `transaction_read_only` foi confirmado como `on`; as transações terminaram com `ROLLBACK`. Cada comando `psql` terminou antes do próximo passo e a sessão SSH foi encerrada. Credenciais e conteúdo de clientes não foram incluídos neste documento.

Uma consulta dos sete dias anteriores encontrou 153.995 registros de invocação. As consultas posteriores usaram janelas móveis; pequenas diferenças de contagem decorrem do processamento concorrente e da mudança do início da janela.

### Latência das chamadas bem sucedidas

Os percentis abaixo excluem falhas locais, que frequentemente duram zero milissegundos e distorceriam a comparação entre modelos.

| Agente e modelo | Chamadas | Média | Mediana | Percentil 95 |
| --- | ---: | ---: | ---: | ---: |
| Verificação de sugestões, Luna | 8.149 | 4,00 s | 3,75 s | 6,06 s |
| Instagram, Luna | 2.825 | 3,52 s | 3,21 s | 5,77 s |
| WhatsApp, Luna | 2.026 | 12,22 s | 12,25 s | 18,53 s |
| Risco, Terra | 258 | 7,46 s | 7,30 s | 11,10 s |
| Checkout, Terra | 35 | 17,63 s | 18,13 s | 29,35 s |

Fonte interna: `ai_agent_invocation_logs`, janela de sete dias, `success = true`. Tempos representam a invocação registrada, não o tempo completo desde o evento, incluindo espera, debounce e persistência.

### Falhas que a troca de modelo não resolve

A categorização dos erros nos sete dias encontrou 136.883 registros de falta de chave de API, 3.608 respostas HTTP 429 e uma resposta HTTP 503. Os erros de chave ocorrem antes de uma chamada autenticada e não representam falhas de qualidade do modelo.

Na janela mais recente de 24 horas, WhatsApp teve 447/447 chamadas bem sucedidas e risco 61/61. Verificação de sugestões teve 802 sucessos e 10.690 erros locais; Instagram, 416 sucessos e 841 erros locais; checkout, seis sucessos e cinco erros locais. Não havia HTTP 429 nessa janela. As ocorrências de limitação de taxa da consulta de sete dias terminaram em 2 de outubro.

Há 11 configurações do verificador de sugestões, mas apenas duas possuem chave própria preenchida; a configuração global desse agente também não tem chave. Isso não significa que todas as demais estejam necessariamente sem acesso, porque existe resolução de credenciais por empresa/provedor. Os próprios erros, contudo, confirmam que a resolução falha em parte das execuções. Os logs recentes das três réplicas de IA também contêm mensagens de falta de chave e sinais de deadlock do PostgreSQL. A causa dos deadlocks não foi isolada nesta análise.

As filas principais de risco, oportunidades, atividades, checkout e transcrição estavam sem mensagens prontas ou sem confirmação. Nas filas de erro havia 4.074 mensagens de transcrição, 3.301 de oportunidades, 71 de atividades, dez de checkout e duas de risco. São estoques observados, sem identificação da idade ou causa de cada mensagem; não foram consumidos nem reprocessados. Jev não transcreve áudio e não corrige automaticamente essas filas.

Os logs foram amostrados em até 5.000 linhas por container das últimas 24 horas. Contagens de sinais podem incluir múltiplas linhas da mesma exceção e não equivalem a número de incidentes.

## Onde aplicar no sistema

| Prioridade proposta | Fluxo | Uso possível | Componente preservado |
| --- | --- | --- | --- |
| Primeiro piloto | Cumprimento de sugestões | `Choice`: fulfilled, unfulfilled, inconclusive; perguntas por evidência | Prazos, notificações e persistência existentes |
| Próximo piloto | Assistente do CRM | Classificar entre intenções já existentes | Permissões, consultas e geração da resposta |
| Posterior | Scorecards de WhatsApp, Instagram e reuniões | `Score` por critério e seleção de evidências conhecidas | Transcrição, resumos, justificativas e revisão |
| Posterior | Risco comercial | Avaliar sinais semânticos como objeção ou compromisso | Regras comerciais, cálculos e recomendações |

Estas prioridades são inferências a partir do código e das medições. Não são resultados de uma execução de Jev no CRM.

O primeiro piloto tem um contrato pequeno e já admite resultado inconclusivo. Entretanto, também devolve uma justificativa textual e IDs de evidência. A justificativa precisaria ser produzida por template ou pelo modelo atual; os IDs deveriam ser selecionados entre candidatos conhecidos. Como o resultado pode marcar uma sugestão como cumprida, o piloto deve começar apenas observando resultados.

O assistente também é um bom encaixe para roteamento, mas hoje extrai nomes livres de conta e usuário. Jev pode escolher entre candidatos retornados por uma busca autorizada ou devolver a classificação ao extrator atual; a troca não é direta. [Roteamento de intenção](https://docs.typesafe.ai/patterns/intent-routing).

A análise de WhatsApp atual reúne texto, sugestões, scorecards e deduplicação semântica na mesma chamada. Separá-la pode acelerar decisões específicas, mas acrescentar Jev e manter a chamada generativa inteira aumenta custo e latência. O piloto precisa medir o fluxo completo e identificar quais chamadas podem efetivamente ser evitadas.

### Confiança exige um contrato diferente

Em `PostgresAnalysisResultStore.cs`, `ai_insights.confidence` recebe `RiskScore / 100`. O banco confirmou essa igualdade nos 258 insights de risco dos sete dias. Nas sugestões de risco, `confidence_score` também recebe a pontuação de risco. Já `CommercialRuleAssessmentService` calcula a confiança do snapshot por uma fórmula de quantidade de evidências e regras. Essas grandezas não são probabilidades calibradas da correção de uma decisão.

A interface agrega confiança de insights e a exibe como percentual. Portanto, reutilizar esses campos para Jev misturaria intensidade do risco, cobertura de evidência e certeza do modelo. Recomendo um registro separado para probabilidade, confiança da decisão, versão do modelo e política aplicada, mantendo os contratos existentes durante a avaliação.

O `confidence` de Choice é calculado a partir da distribuição; não significa diretamente taxa de acerto. Com três opções e probabilidade máxima de 0,90, a confiança é 0,85. Noul não retorna confiança separada. O limiar atual de 80 do verificador não deve ser transferido automaticamente para o novo fornecedor. [Confiança](https://docs.typesafe.ai/confidence).

## Estimativa de custo do primeiro piloto

Nas 8.149 verificações bem sucedidas foram registrados 80.825.867 tokens de entrada, incluindo 5.517.555 em cache, e 2.102.329 de saída. Usando as tarifas armazenadas em `ai_rate_cards` para Luna, a estimativa é US$ 17,69 nesse período. Não é conciliação da fatura do fornecedor; as tarifas internas estavam datadas de 15 de setembro.

Se Jev recebesse exatamente a mesma contagem de entrada, o preço publicado daria US$ 3,39, cerca de 80,8% abaixo dessa estimativa. É uma simulação: tokenizadores, perguntas, tamanho do contexto, justificativas e fallback mudam o consumo. Durante avaliação paralela, esse valor seria custo adicional. [Preço de Jev](https://docs.typesafe.ai/models).

O verificador teve entrada média de 9.919 tokens e máxima de 44.446; 158 chamadas passaram de 32 mil tokens. Risco teve média de 36.624 e checkout de 49.653. Essas contagens do fornecedor atual sinalizam necessidade de reduzir contexto; não permitem afirmar exatamente quais entradas excederiam o tokenizer de Jev.

## Integração proposta e critérios de avaliação

1. Criar um adaptador .NET com `HttpClient` para `POST https://api.typesafe.ai/v1/systemone`, isolado dos clientes OpenAI atuais. A API tem formato próprio, autenticação Bearer e tratamento de 429/529; apenas mudar o nome do modelo não funciona. [API](https://docs.typesafe.ai/api).
2. Manter a ativação desligada por padrão e habilitar por empresa e agente. Fixar a versão de Jev avaliada e registrar a versão efetivamente retornada.
3. Começar em modo de observação: comparar sem alterar sugestões, oportunidades, scorecards ou notificações. Preservar regras, contexto autorizado, isolamento entre empresas e validação de IDs.
4. Validar respostas, limites e cancelamento. Em indisponibilidade, contexto incompatível ou baixa confiança, manter o caminho atual. Evitar retries ilimitados e cobranças repetidas.
5. Adaptar o registro de tokens e cadastrar tarifas específicas. Hoje o validador e `NormalizeProvider` aceitam somente OpenAI; a medição de créditos depende de tarifa por provedor/modelo e não registra consumo quando não encontra tarifa. O modo de observação comercial não deve debitar créditos de clientes.
6. Avaliar um conjunto inicial proposto de pelo menos 300 exemplos em português, com casos difíceis, evidências anteriores à sugestão, múltiplos candidatos e dados de empresas distintas. Usar revisão humana como referência; concordância com o modelo atual não basta.

Medir precisão por resultado, falsos cumprimentos, proporção inconclusiva, calibração, percentis de latência, custo por resultado correto e frequência de fallback. Há 2.130 scorecards gerados nos sete dias sem revisão registrada e somente 20 feedbacks de sugestões nessa janela; esses dados ainda não fornecem uma referência humana suficiente para validar a qualidade.

Datas, prazos, valores, contagens e pesos continuam calculados em código. A documentação reconhece limitações com precisão numérica, comparação de datas, contexto irrelevante, conteúdo adversarial e geração textual. [Limitações de Jev 1.13](https://docs.typesafe.ai/model-jaggedness/jev-1.13).

## Entrega e pendências

Esta entrega contém a análise e uma proposta concreta de piloto, sem integração ativa, deploy ou commit. Os fluxos que já funcionam foram preservados. Não foi feita chamada a Jev nem enviado conteúdo do CRM ao fornecedor; faltam uma chave/acesso TypeSafe e a execução da avaliação proposta.

A revisão operacional de credenciais ausentes e deadlocks deve ser tratada separadamente antes de comparar a taxa de falhas com outro provedor. Alterar credenciais, reprocessar filas ou corrigir concorrência seria um trabalho distinto desta análise.

Referências locais principais: `CrmAi.Application/SuggestionCompletionVerificationModels.cs`, `CrmAi.Infrastructure/Persistence/SuggestionCompletionVerificationHostedService.cs`, `CrmAi.Application/WhatsappConversationAnalysis/WhatsappConversationAnalysisAgent.cs`, `CrmAi.Application/CommercialRuleAssessmentService.cs`, `CrmAi.Infrastructure/Persistence/PostgresAnalysisResultStore.cs` e `CrmAi.Infrastructure/Persistence/PostgresAiAgentInvocationLogStore.cs`. No backend: `ExternalServices/OpenAiAssistantIntentClassifier.cs`, `ApplicationServices/AiAgentSettingsService.cs` e `Validators/AiAgentSettingsValidators.cs`. No frontend: `src/crm/opportunity-workspace.tsx`.
