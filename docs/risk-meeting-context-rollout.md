# Contexto de reuniões nas análises de risco

A análise de reunião/ligação gera `riskContext` na chamada já existente. Não há
uma chamada adicional para resumir. Os fatos incluem decisões, compromissos,
prazos, objeções, contradições e resoluções, cada um com evidência literal.
Isso acrescenta tokens de saída à análise original; a economia total precisa
incluir esse custo e o número de reutilizações nas análises de risco.

O resultado persistido em `conversation_analysis_results.analysis_json` recebe
`storedRiskContext`, com versão da política e SHA-256 da transcrição exata.
Não há migração nem alteração do resumo apresentado ao usuário. A leitura
consulta somente análises concluídas e atuais da mesma empresa, gravação e
oportunidade. Textos editados, evidências inventadas, versão antiga, baixa
confiança ou cobertura insuficiente impedem reutilização.

O contexto compacto mantém os demais blocos do CRM e os fatos sustentados de
cada reunião elegível. Cada evidência contém offsets e texto vizinho; todas
as ocorrências de uma citação são preservadas. Reuniões sem ganho de tamanho
continuam completas. A transcrição completa permanece disponível, inclusive
as confirmações depois dos primeiros 6.000 caracteres que antes eram cortadas.
Este caminho completo pode custar mais que a antiga entrada truncada.

## Configuração

`OpenAI:RiskMeetingContextMode` aceita:

- `full` (padrão): uma chamada com o contexto completo.
- `shadow`: primeiro a chamada completa; depois a compacta para comparação.
  Retorna sempre o resultado completo. A segunda chamada aumenta o consumo
  durante a avaliação e suas falhas não descartam o primeiro resultado.
- `compact`: uma chamada compacta quando houver resumo elegível e ganho de
  tamanho. Resultado incerto, baixa confiança, recusa/JSON inválido ou ids de
  evidência inexistentes causam uma única chamada com contexto completo.
  Erros HTTP/transporte e cancelamentos não iniciam novas tentativas.

`OpenAI:RiskMeetingContextCompanyIds` delimita as empresas participantes.
Sem empresa explicitamente incluída, utiliza `full`, mesmo que o modo global
esteja configurado como `shadow` ou `compact`.

Exemplo de variáveis para um piloto controlado:

```text
OpenAI__RiskMeetingContextMode=shadow
OpenAI__RiskMeetingContextCompanyIds__0=<id-da-empresa-do-piloto>
```

Após avaliar os resultados desse piloto, `compact` habilita a redução para
essas empresas. `full` reverte imediatamente sem alterar os dados salvos.
Reuniões antigas seguem completas até uma análise normal gerar resumo elegível;
não foi agendado reprocessamento em massa.

## Critérios de avaliação

Use `context-optimization-usage.sql` para tokens reais, chamadas de fallback
e pares de comparação. `riskContextComparisonId` associa as chamadas, e os
logs contêm ambas as entradas/saídas. Concordância de nível e diferença de
score são indicadores, não prova de equivalência semântica.

Revise pares da mesma oportunidade verificando riscos omitidos ou inventados,
quem solicitou/executa cada compromisso, datas/valores, negações, contradições
e confirmações posteriores. Inclua reuniões com responsáveis ambíguos, sem
resumo elegível e com correções de transcrição. Só adote `compact` para o
piloto após essa avaliação; os testes HTTP simulados validam o mecanismo,
não a qualidade dos modelos em produção.

Compare o consumo total por oportunidade/evento, incluindo geração dos fatos,
fallbacks e saída. O experimento `shadow` precisa ser contabilizado separado
do consumo esperado de `compact`, pois executa deliberadamente duas análises.
