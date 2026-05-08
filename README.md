# CrmAi

API/worker .NET 9 para consumir eventos de oportunidade no RabbitMQ, montar o contexto comercial no PostgreSQL e executar agents de analise.

## Arquitetura

- `CrmAi.Domain`: modelos puros de evento, contexto de oportunidade e resultado de risco.
- `CrmAi.Application`: portas, caso de uso de processamento e `RiskAnalysisAgent`.
- `CrmAi.Infrastructure`: adaptadores RabbitMQ, PostgreSQL e persistencia de insights.
- `CrmAi.Api`: host ASP.NET Core com worker em background e health check.
- `CrmAi.Tests`: testes unitarios do agent.

## RabbitMQ

A fila duravel configurada e `crm.ai.risk-analysis`.

Ela e ligada aos exchanges fanout:

- `crm.events.opportunity.activity.created`
- `crm.events.opportunity.note.created`
- `crm.events.opportunity.stage.changed`

URI padrao:

```text
amqp://admin:rabbitBrokerPasS@rabbitmq:5672/
```

## PostgreSQL

Connection string padrao:

```text
Host=localhost;Port=5432;Database=crmdb;Username=crm_user;Password=crm_pass
```

O contexto de analise busca oportunidades, fase, notas, contatos, usuarios, atividades e historico seguindo o schema das migrations do backend CRM.

Tambem carrega as regras ativas de `commercial_analysis_metric_rules` vinculadas ao `commercial_analysis_settings` ativo mais recente. Essas regras entram no calculo de `health_score`, `confidence_score` e no score de risco do agent.

## Resultado

Cada processamento grava um registro em `ai_insights` com `kind = risk-analysis` e mensagem JSON neste formato:

```json
{
  "riskLevel": "HIGH",
  "riskScore": 82,
  "reasons": [],
  "recommendations": [],
  "snapshot": {
    "snapshotAt": "2026-05-06T20:18:07Z",
    "daysInStage": 0,
    "activitiesOpen": 1,
    "activitiesOverdue": 0,
    "lastInteractionDays": 0,
    "lastInteractionAt": "2026-05-06T20:18:07Z",
    "healthScore": 90,
    "confidenceScore": 95
  },
  "triggerEvent": "opportunity.activity.created"
}
```

Quando o risco calculado e alto, `opportunities.risk` tambem e marcado como `true`.

Para cada evento processado, o worker atualiza o snapshot diario em `opportunity_analysis_snapshots` com `snapshot_source = daily` para a oportunidade. Se ainda nao houver snapshot no dia UTC do evento, um novo registro e criado.

## Rodar

```bash
dotnet restore
dotnet run --project src/CrmAi.Api
```

Endpoints:

- `GET /`: status basico do servico.
- `GET /health`: valida conexao com PostgreSQL.

## Testes

```bash
dotnet test
```
