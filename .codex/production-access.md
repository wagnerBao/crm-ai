# Diagnóstico de produção e reprodução local

Use este procedimento quando a tarefa exigir consultar logs, serviços ou dados reais
para analisar ou corrigir o sistema. Faça alterações pontuais no código e preserve
funcionalidades existentes. Esta rotina não inclui commit, push, deploy, reinício de
serviço ou mudança de dados em produção; essas ações exigem um pedido explícito.

## Credenciais atuais e caminhos dos projetos

Não coloque credenciais neste arquivo, em AGENTS.md ou nas definições dos agentes.
Resolva a fonte local nesta ordem:

1. Caminho informado pela variável `SKOPOS_PRODUCTION_ACCESS_FILE`, quando definida.
2. `.agent-local/production-access.json` na raiz do repositório da tarefa.
3. Referência `accessFile` em `.agent-local/production-access-reference.json` na mesma raiz.
4. Em worktree sem esses arquivos, resolva o checkout principal com
   `git rev-parse --path-format=absolute --git-common-dir`. A pasta que contém o diretório
   Git comum é o checkout principal nos worktrees gerenciados usuais; confira o caminho
   antes de procurar nele os arquivos acima. Em arranjos diferentes, peça o caminho da fonte.

Leia a fonte atual somente quando necessário. O objeto `ssh` contém host, usuário e senha;
`database.connectionString` contém a string para consultas PostgreSQL, com o nome de
variável de aplicação indicado em `database.environmentVariable`. `projects` contém os
caminhos locais de frontend, API, consumers, IA, webhooks e website. Não presuma que o caminho
do checkout principal corresponde à worktree autorizada para edição.

Senhas podem mudar: não mantenha cópias nas instruções, na memória do agente ou em prompts
compartilhados. Se a autenticação falhar, releia a fonte uma vez; se continuar falhando,
solicite a atualização da credencial, sem insistir com tentativas repetidas.
Se os arquivos não existirem, peça o caminho/atualização; não recupere senhas de logs ou do Git.

O arquivo canônico fica no checkout principal do frontend. Os demais projetos guardam
somente uma referência local ao arquivo. Para trocar uma senha, edite a fonte canônica;
os agentes usarão o valor atualizado na próxima tarefa. Não altere credenciais de produção
no servidor nem nas configurações de implantação como consequência dessa edição local.

## Consulta do sistema em produção

- Use o acesso SSH para ler status, configuração necessária e logs dos serviços afetados.
  Restrinja intervalo de tempo, volume e tenant/conversa envolvidos na investigação.
- No banco, utilize a connection string local para consultas limitadas. Prefira transação
  explicitamente `READ ONLY`, `statement_timeout` e limite de linhas apropriados ao diagnóstico.
  Não rode `EXPLAIN ANALYZE` em comandos com efeitos colaterais.
- Não execute migrations, seeds, suítes de teste, workers, chamadas de IA ou envio de mensagens
  contra produção. Não escreva dados, reinicie serviços ou faça deploy sem pedido explícito.
- Não desabilite verificação da identidade do servidor SSH/TLS para facilitar a conexão.
- Leia segredos diretamente do arquivo em variáveis do processo/cliente. Não use senhas como
  argumentos de linha de comando, em URLs, no histórico, em `set -x`, ou em comandos enviados
  como texto ao servidor. Use mecanismos de autenticação apropriados ao cliente disponível.
- Não imprima o arquivo de acesso ou a connection string para testar a configuração.
  Retorne apenas estado da conexão e evidências necessárias, com dados sensíveis removidos.

## Cópia de dados para reprodução

- Extraia somente tabelas/linhas/colunas necessárias, incluindo tenant e relações suficientes
  para reproduzir o problema. Preserve a integridade dos vínculos da amostra.
- Guarde exports, dumps, logs brutos e arquivos de autenticação temporários exclusivamente
  em `.agent-local/` ou pasta temporária privada fora de repositórios. Use diretórios `0700`
  e arquivos `0600`. Não use `public/`, `artifacts/`, fixtures versionadas ou anexos do chat.
- Remova credenciais e anonimimize dados pessoais que não sejam necessários à reprodução,
  preservando relações relevantes. Dados brutos e segredos não devem seguir para subagentes,
  provedores de IA ou relatórios; compartilhe apenas amostras sanitizadas e resumos.
- Importe a amostra em banco local/descartável aceito pela fixture correspondente. Não copie
  connection strings, endpoints, tokens ou configuração operacional de produção para o teste.
  Mantenha filas, canais e integrações externas isolados para impedir efeitos reais.
- Não afrouxe validações que exigem banco de teste para fazer uma cópia funcionar. Se forem
  necessários dados persistentes para regressão, transforme a amostra em fixture sintética.

## Encerramento obrigatório

Sempre use `try/finally`, `using`/`await using`, context managers ou a construção equivalente
do cliente. Ao terminar, inclusive em erro ou cancelamento:

1. Termine transações abertas com rollback quando usadas apenas para leitura.
2. Feche cursores/readers, conexões e pools criados especificamente para a tarefa. Em .NET,
   descarte também o data source próprio da tarefa quando ele mantiver um pool.
3. Termine o processo `psql`/cliente, sessões SSH, túneis e encaminhamentos abertos pela tarefa.
   Não deixe uma sessão interativa conectada depois de concluir a investigação.
4. Remova arquivos temporários de autenticação e variáveis de credenciais da sessão de shell
   quando não forem mais necessários. Mantenha somente a fonte local gerenciada e amostras
   necessárias no diretório ignorado. Não encerre conexões ou processos de outros trabalhos.
5. Informe o que foi consultado, conclusões e verificações realizadas, incluindo a confirmação
   de que as conexões da tarefa foram encerradas; nunca inclua senhas ou dados brutos no relato.

## Proteção no Git e no build

`.agent-local/` deve estar ignorado no Git e no contexto Docker de cada projeto.
Confirme com `git check-ignore` antes de gravar dados. Não use `git add -f` nesses arquivos.
Uma regra de ignore não remove arquivos já rastreados: confira `git ls-files` antes de assumir
que um arquivo está protegido. Não inclua o diretório em uploads, imagens ou cópias de worktrees.

As regras de acesso ficam versionáveis; os valores e as referências locais ficam ignorados.
Credenciais ausentes devem permanecer ausentes durante a instalação/reaplicação das regras.
