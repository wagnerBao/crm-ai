# Estado atual dos agentes — 13/10/2026

Corte: **12:07:36 BRT**, desde 00h local. Consultas em transação Repeatable Read READ ONLY, timeout de 30s e rollback; nenhuma mudança em produção. Dados agregados e presença booleana de credenciais, sem chaves ou mensagens de clientes.

- Hoje: **19.561.532 tokens**, 2.532 chamadas com medição canônica (eventos + logs sem evento), **US$ 5,5204 estimados com tarifas internas**.
- Tentativas crm-ai: 23.147; 2.301 sucessos; 20.846 falhas. Esse universo difere das chamadas canônicas.
- Falhas hoje: 14.096 `credit_balance_exhausted`, 6.743 `missing-local-key`, 7 sem classificação.
- Última hora: 747 tentativas, 172 sucessos, **575 falhas, todas por chave ausente**. Nenhuma falha de saldo nessa janela; os grupos com crédito retomaram execuções a partir de ~07:53–08:04. Isso indica recuperação observada, sem garantia futura do saldo.

## Bloqueios que continuam

| Empresa | Agente | Falhas hoje | Última hora | Chave própria/global/fallback suportado |
| --- | --- | ---: | ---: | --- |
| Multiplix | Verificador de sugestões | 6.524 | 551 | Nenhuma; WhatsApp sem chave; Instagram tem chave própria |
| Wagner Brum | Análise de Instagram | 172 | 20 | Nenhuma; WhatsApp tem chave própria |
| Academia Digital Pro | Análise de Instagram | 47 | 4 | Usa setting global ativo, sem chave |

Todas essas configurações estão ativas e sem sucesso hoje. Presença de chave é apenas booleana; não mede validade/saldo. O fallback no código local usa chave própria, global do mesmo agente e dependência específica (WhatsApp no verificador); ele não implica autorização para mover credenciais entre empresas.

## Gargalos de tokens confirmados

- Verificador: 12.281.068 tokens em 978 chamadas. 587 reavaliações no mesmo dia, 7.931.523 tokens; 81 recuperações completas, 1.329.284 tokens. 559 chamadas atingiram 120 evidências, 9.326.964 tokens. Nenhum pedido exatamente repetido hoje.
- WhatsApp: 4.413.415 tokens em 513 chamadas; **513/513** duplicam `additionalContext` entre input e instructions, 609.875 caracteres repetidos.
- Staff e AVEP: 14.164.877 tokens (72,41% do total); prioridades para medir redução de contexto e frequência.
- Pendências no snapshot: 4.755; 1.802 elegíveis; 2.932 com mais de sete dias. 2.481 com fingerprint. Fingerprints são hashes SHA256 hexadecimais; não identificam a versão da política.

## Limite da verificação de versão

Logs observam `lossless-context-v1` no verificador/checkout e `risk-meeting-context-v1` no risco. Não foi observado `evidenceSelectionVersion` nem commit/build SHA. `/health` no código local retorna apenas status. O workflow local publica imagem por SHA em push e atualiza Swarm; isso não demonstra qual SHA está rodando. A ausência da seleção na telemetria é observação, não prova isolada de ausência de funcionalidade. Não houve acesso/modificação via SSH.

Arquivos reproduzíveis: `today_snapshot.sql`/`.jsonl`, `today_diagnostics.sql`/`.jsonl` e `current_state_summary.json`. As consultas de pendências e de filas são snapshots do estado na leitura, não reconstrução histórica do corte. Valores em USD não foram conciliados com fatura.
