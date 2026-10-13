# Verificação da migration no servidor

Consulta em **13/10/2026 às 13:21:38 BRT**, no banco do servidor usado pelo diagnóstico do CRM, com conexão primária e transação `REPEATABLE READ READ ONLY`, encerrada com `ROLLBACK`.

**Resultado: a estrutura corresponde à versão atual da migration `20261013_create_ai_invocation_cooldowns.sql`. Não há ajuste estrutural pendente dessa migration no banco consultado.**

| Item | Resultado observado |
| --- | --- |
| Tabela | `public.ai_invocation_cooldowns` existe e resolve pelo search path da conexão |
| Colunas obrigatórias | `company_id uuid`, `agent_key text`, `configuration_fingerprint text`, `blocked_until timestamptz`, `reason text` e `updated_at timestamptz` |
| Permissão da tentativa de retomada | `probe_id uuid`, nullable |
| Padrão de atualização | `updated_at DEFAULT now()` |
| Chave primária | `(company_id, agent_key, configuration_fingerprint)`, constraint validada |
| Status de WhatsApp e Instagram | `varchar(32)`, sem CHECK na coluna que restrinja o valor `deferred` |

O resultado confirma a estrutura no banco; não identifica se ela foi criada por execução manual ou pelo startup, nem o SHA do worker em execução. As permissões registradas no resultado pertencem à conexão usada nesta consulta, não são uma verificação independente das credenciais efetivas do worker.

Nenhum DDL, atualização de dados, deploy ou chamada ao provedor foi executado nesta verificação. O worker atualizado continua executando o bootstrap idempotente na inicialização.

Evidências reproduzíveis: [consulta SQL](server_migration_check.sql) e [resultado agregado](server_migration_check.jsonl).
