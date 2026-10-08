# Verificação de sugestões após novas interações

Alteração local de 08/10/2026, restrita ao verificador de cumprimento das sugestões. Sem alteração de configurações das empresas, migração, commit ou publicação em produção.

## Comportamento

- A primeira verificação continua seguindo os critérios e horários existentes. Sem interação posterior à criação, registra `unfulfilled` sem chamar a IA.
- Após um resultado `unfulfilled` ou `inconclusive`, o worker exige evidência posterior à sugestão e mais recente que a última verificação. O vencimento do temporizador, a virada do dia e um novo agendamento com os mesmos dados não provocam outra análise.
- Contam mensagens de WhatsApp/Instagram, transcrições prontas, atividades reais criadas ou atualizadas, notas, mudanças de oportunidade/histórico e evidências de reuniões, dentro do escopo da empresa e do contato/oportunidades relacionadas. As opções de contexto da empresa continuam controlando as evidências efetivamente utilizadas.
- Atividades de análise com tipo `agent-skopos` e históricos de criação automática dessas atividades não contam como interação nem invalidam o cache. Continuam disponíveis no contexto quando uma análise realmente é necessária.
- Atualizações técnicas que preservam o conteúdo, como metadados de entrega de mensagem, podem ocasionar uma comparação local de fingerprint; não geram nova chamada de IA, histórico de verificação ou incremento de tentativas.
- Os temporizadores existentes e os publicadores de notificações continuam funcionando. Uma prioridade vencida ainda ausente pode ser restaurada com o resultado em cache, sem nova chamada de IA. Falhas do provedor continuam permitindo tentativas com o mesmo contexto, conforme o intervalo de recuperação existente.
- A referência temporal salva é o início da verificação. Uma interação que chega durante a chamada de IA permanece elegível para a próxima passagem.

Não há mudança na regra de inatividade que cria sugestões. Esta alteração controla a **reverificação de uma sugestão já criada**. Mantém os intervalos de agendamento existentes; uma nova atividade fica elegível quando o temporizador permitir, e os fluxos que já solicitam verificação imediata continuam podendo fazê-lo.

## Implementação e validação

`SuggestionVerificationActivityGate` filtra as candidatas antes do claim e carregamento completo do contexto. `SuggestionVerificationCache` compara evidências e configurações sem invalidar o resultado apenas pelo relógio. Não é necessária mudança de esquema.

A compilação JIT do PostgreSQL fica desativada somente na transação curta de claim. Uma consulta somente leitura, sem candidatas vencidas na amostra, caiu de aproximadamente 1.086 ms (incluindo compilação JIT) para 12,8 ms; esse resultado não representa o desempenho sob todas as cargas.

Os testes de integração executam o processor e a consulta reais sobre tabelas temporárias de sessão, `search_path=pg_temp` e transações somente leitura. Não usam dados de clientes, chamadas de IA ou RabbitMQ. Cada teste descarta seu datasource e fecha a conexão.

Validação concluída: solução compilada sem erros ou avisos; 227 testes aprovados, incluindo os 12 cenários de integração desta regra. Outros 11 testes, de RabbitMQ/estado de análise, foram ignorados por dependerem de infraestrutura local não configurada. Uma consulta final a `pg_stat_activity` confirmou zero conexões remanescentes dos testes.

Para executar os testes de banco, fornecer `SUGGESTION_VERIFICATION_TEST_DATABASE` pelo ambiente e executar:

```sh
dotnet test tests/CrmAi.Tests/CrmAi.Tests.csproj --no-restore
```

Sem essa variável, os testes de PostgreSQL são ignorados; os demais continuam disponíveis. Não gravar credenciais em arquivos do projeto.

Após a publicação, comparar chamadas e tokens do agente `suggestion-completion-verification`, repetições por `suggestionId`, sugestões cumpridas e notificações de prioridade/resposta. O relatório histórico de consumo está em `docs/diagnostics/skopos-token-situations-2026-10-08.md`. A economia de produção ainda depende da publicação e de uma nova medição; repetições da mesma sugestão com evidências realmente diferentes continuam legítimas.
