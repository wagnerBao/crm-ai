# Instruções de desenvolvimento do crm-ai

<!-- BEGIN:skopos-agent-sessions -->

## Coordenação de agentes de desenvolvimento

- Leia somente arquivos e documentação necessários ao fluxo alterado. Localize o produtor,
  contrato, processamento e persistência antes de implementar mudanças entre serviços.
- Em tarefas complexas, delegue partes independentes aos especialistas disponíveis, até três
  subagentes simultâneos. Tarefas pequenas ficam com um agente; especialistas não redelegam.
- Cada delegação informa objetivo, repositório/worktree real, arquivos permitidos, contratos,
  invariantes, critérios de aceite e saída esperada. Um único escritor por arquivo compartilhado.
- Subagentes podem compartilhar a pasta. Use sessões/worktrees separados para escrita paralela
  sobreposta ou serialize as edições. Não mude a branch de uma pasta compartilhada.
- Worktrees começam em estado commitado. Não incluem automaticamente alterações locais,
  dependências, configurações privadas ou os demais repositórios. Use caminhos reais da tarefa.
- Os papéis nativos skopos_coordinator, skopos_frontend, skopos_ux, skopos_api, skopos_data,
  skopos_consumers, skopos_ai, skopos_webhooks e skopos_qa estão em .codex/agents.
  Se algum papel não estiver carregado, use um agente genérico com o mesmo escopo ou execute
  sequencialmente. O limite de concorrência é por sessão, não global.
- Preserve empresa/tenant, permissões, idempotência e compatibilidade dos contratos HTTP,
  eventos e schema. Alterações de banco exigem conferir todos os serviços leitores/escritores.
- Revise o diff integrado e execute verificações proporcionais ao comportamento e risco.
  Distinga PASSOU, FALHOU e NÃO EXECUTADO; reporte testes ignorados e pré-requisitos ausentes.
- Execute testes e processamento em ambiente isolado. Diagnóstico de produção em leitura
  segue a seção de acesso a produção e .codex/production-access.md.
- Retorne decisões, arquivos, verificações e pendências em resumo curto; não replique logs
  extensos nem o histórico inteiro da sessão entre especialistas.

## Análises de IA

- Trace evento/scheduler → contexto → agente/cliente → schema de saída → persistência.
  Comece em src/CrmAi.Application/AnalysisPipeline.cs e nos registros de dependências.
- Preserve a separação entre análise de oportunidade/risco e atividades/conversas.
  Atendimento executado nos consumers não deve ser duplicado aqui.
- Reutilize runtime settings, guards/cooldowns, logs e medição existentes. Alterar contexto,
  prompt, ferramenta ou schema exige avaliação representativa com evidências de qualidade.
- Dados relevantes e evidências não devem ser descartados só para reduzir tokens. Diferencie
  custo/latência medidos de estimativas; não dispare chamadas pagas como teste padrão.
- Confira CrmAi.sln e as fixtures antes de dotnet test. Há requisitos específicos de banco,
  incluindo AI_INVOCATION_GUARD_TEST_DATABASE; não troque por banco de produção.
- skopos_ai implementa; skopos_data verifica schema e skopos_qa revisa avaliações/regressões.

<!-- END:skopos-agent-sessions -->

<!-- BEGIN:skopos-production-access -->

## Diagnóstico de produção

- Quando o requisito exigir verificar produção, siga `.codex/production-access.md`.
  Consulte logs, estado dos serviços e banco em leitura; extraia somente os dados necessários
  para reproduzir o problema em ambiente isolado. Preserve as funcionalidades existentes.
- As credenciais atuais e caminhos dos projetos ficam em arquivos locais ignorados em
  `.agent-local/`. Leia a fonte atual a cada tarefa; senhas podem mudar. Não grave segredos
  em instruções versionadas, código, relatórios, comandos registrados ou mensagens entre agentes.
- Testes automatizados, workers e migrations continuam restritos a ambientes isolados;
  acesso de diagnóstico não autoriza escrita, reinício ou deploy em produção.
- Sempre finalize conexões de banco/SSH e sessões/transações abertas, inclusive após erro
  ou cancelamento. Não faça commit, push ou deploy nesta rotina sem pedido explícito.

<!-- END:skopos-production-access -->
