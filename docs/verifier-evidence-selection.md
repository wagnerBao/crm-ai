# Seleção de evidências do verificador

O verificador pode enviar um subconjunto dos registros já coletados, sem outra
chamada de IA para selecionar nem uso de embeddings. A seleção é determinística
e ocorre antes da compactação de textos repetidos e IDs. O registro salvo da
verificação usa os IDs físicos das evidências.

## Seleção

Apenas mensagens de WhatsApp/Instagram com conversa identificada podem ser
omitidas. O seletor agrupa mensagens da mesma conversa/canal em episódios,
separados por mais de 30 minutos, e mantém o episódio inteiro quando encontra:

- Correspondência com o assunto da sugestão, descrição ou notas do payload.
- Pedido anterior à sugestão ou mensagem no instante de criação.
- Confirmação, negação, pendência, correção, compromisso ou referência temporal.
- Mensagem curta/ambígua, mídia, pergunta, URL, valor ou número.
- Início/fim da conversa coletada, primeira mensagem posterior ou última
  mensagem de cada direção.

Atividades, notas, reuniões, oportunidades e histórico permanecem completos
dentro do conjunto coletado. Nomes conhecidos de participantes não contam como
assunto. Conversas desconhecidas, formatos não reconhecidos, resumos que atingem
o corte de 1.200 caracteres e ações genéricas são mantidos. A direção e o texto
dos registros sobreviventes não são reescritos. Nas chamadas selecionadas,
conversas recebem aliases `c1`, `c2`, etc.; os GUIDs usados pelo seletor não
aumentam o contexto da API.

Não há uma lista top-N de evidências selecionadas. A seleção só é enviada se
economizar pelo menos 512 caracteres e 10% em relação à representação completa
já otimizada, considerando as instruções extras. Isso é tamanho de contexto,
não estimativa de tokens faturados.

## Resultado e recuperação

O esquema da chamada selecionada exige `needsFullEvidence`. Somente decisões
`fulfilled`/`unfulfilled` com confiança >= 90, flag explicitamente falsa e ao
menos uma evidência posterior válida são aceitas. Ausência de prova no
subconjunto não basta para concluir que a ação ficou pendente.

Inconclusão, flag verdadeira/ausente, baixa confiança, IDs inválidos, prova só
anterior ou saída recusada/JSON inválido causam uma única chamada com TODOS os
registros originalmente coletados, em formato expandido e com IDs físicos.
Erros HTTP/transporte, falta de créditos e cancelamento não criam essa tentativa
adicional. A validação final continua impedindo cumprimento com ID inventado.

O cache usa o conjunto original inteiro, incluindo texto, direção contida no
resumo, datas, tipo e conversa das evidências omitidas, além da sugestão, prazo,
modelo, instruções, política e modo efetivo. Mudar um registro omitido ou trocar
de modo invalida o resultado anterior. `SourceStreamId` é interno e participa
explicitamente do fingerprint, mesmo sem ser serializado no contexto da API.

## Ativação e avaliação

Configuração por empresa, sem migração:

```text
OpenAI__SuggestionEvidenceSelectionMode=shadow
OpenAI__SuggestionEvidenceSelectionCompanyIds__0=<id-da-empresa-do-piloto>
```

Modos disponíveis:

- `full` (padrão): mantém todas as evidências coletadas e a compactação existente.
- `shadow`: obtém o resultado completo primeiro, depois testa a seleção e retorna
  sempre o resultado completo. Conta todas as chamadas; pode haver até três se
  o caminho completo já precisar expandir suas referências.
- `selected`: envia a seleção quando elegível, com no máximo uma recuperação
  completa. Sem elegibilidade/ganho, usa o caminho completo existente.

Empresas fora da lista permanecem em `full`. Reverter o modo não altera os dados
salvos. A comparação custa mais durante o piloto e fica desativada por padrão.

Os testes simulados verificam preservação de registros, datas, responsáveis,
negações, confirmações, IDs e comportamento de recuperação. Correspondência
lexical não prova irrelevância semântica. Antes de ativar `selected`, revise os
pares reais ligados por `evidenceSelectionComparisonId`, incluindo Diego como
responsável interno/Pierre como contato, mensagens curtas após mudança de
assunto e ações de botão/0800 tratadas separadamente. Concordância do status
é um indicador; revise também as justificativas e as evidências.

As consultas de `context-optimization-usage.sql` incluem tokens reais de todas
as chamadas, decisões divergentes, suporte do resultado selecionado e volume
de registros enviado. Meça o total por sugestão/evento, incluindo recuperações,
e compare cargas equivalentes.

Esta mudança mantém o escopo e os limites existentes da coleta: janela desde
24 horas antes da sugestão, até 120 registros e resumo de até 1.200 caracteres
por registro. “Completo” aqui significa o conjunto originalmente coletado,
não todo o histórico do banco; a recuperação não ultrapassa esses limites.
