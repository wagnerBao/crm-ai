# Contexto imobiliário nas análises

Implementação E1 de `FRONT_SAAS/DOCS/implementacao-modulo-imobiliario/08-analises-e-indicadores.md`, verificada em 15/10/2026. Não houve implantação, ativação de agentes ou uso de provedor real.

## Contrato e autorização

`OpportunityEvent.CompanyId` é opcional e aditivo. Eventos antigos continuam desserializando. A oportunidade armazenada determina o tenant; o envelope e o hint legado `Data.companyId`, quando fornecidos, precisam corresponder antes de carregar configurações e filhos. Eventos de outro tenant, IDs contraditórios e hints inválidos retornam contexto nulo. O processamento agendado associa eventos legados ao tenant do lease validado antes de consultar o repositório.

Eventos de WhatsApp/Instagram com `conversationId` também precisam referenciar uma conversa armazenada no mesmo tenant da oportunidade. No serviço Instagram, a empresa da conversa valida envelope/payload e propriedade da oportunidade antes das configurações e da deduplicação; ela é propagada ao enriquecimento. Uma oportunidade rejeitada não vira análise de contato por fallback. A deduplicação, o join do contato e a atualização da oportunidade ficam limitados à empresa da conversa.

`OpportunityAnalysisContext.RealEstate` é o último argumento opcional. Sua ausência mantém o caminho anterior. As configurações existentes não recebem seleção automática de novas entidades.

As chaves opt-in reconhecidas pelo repositório e pelos builders são:

- `real_estate_demand`
- `real_estate_offers`
- `real_estate_visits`
- `real_estate_proposals`
- `company_knowledge`

O catálogo editável e sua validação pertencem a `AiAgentSettingsService` no backend. Os defaults do crm-ai permanecem os existentes. O repositório exige módulo `real_estate` habilitado, regras publicadas e pelo menos uma chave selecionada em um agente analítico ativo. Sem a migration operacional, consultas antigas continuam funcionando quando as chaves novas não estão selecionadas. Com as chaves novas, a ausência de `company_modules` também produz contexto nulo.

Cada consulta usa tenant e oportunidade. Ofertas vêm exclusivamente de `real_estate_opportunity_properties`; não se percorre a carteira completa para enriquecer uma oportunidade. Procura, visita e proposta sem oportunidade continuam válidas operacionalmente e não criam uma oportunidade fictícia para análise.

## Projeção e orçamento

| Bloco | Limite SQL e builder | Dados incluídos |
| --- | --- | --- |
| Procura | 5 | Finalidade, estado, tipo(s) até 10, cidade/bairro, orçamento, requisitos de área/quartos e revisão/estado/versão da qualificação. |
| Oferta vinculada | 10 | IDs, código textual, tipo, localização pública, finalidade, valor/moeda, despesas e periodicidades, validade, confirmação e revisões da oferta e do imóvel. |
| Visita | 10 | IDs, estado, intervalo confirmado quando existente, responsável, revisão e data. |
| Proposta | 5 | IDs, estado, valor/moeda, validade, revisão e data. |
| Conhecimento | 5 | Versão publicada, título até 160 caracteres e trecho até 600 caracteres, filtrados pela finalidade da procura. |

Não são serializadas entidades operacionais completas nem documentos, partes, contatos privados, endereços livres legados, notas livres da procura/visita, condições privadas de proposta ou rascunhos editoriais. A localização estruturada usa a menor precisão entre `addressPrecision` publicado e `addressVisibility` do imóvel: cidade, bairro, rua sem número ou endereço completo. Número, complemento, CEP, coordenadas e identificador de lugar entram somente em `full`, com autorização vigente e carteira confirmada. A ausência dessa confirmação deixa a localização vazia, inclusive para uma oferta histórica mantida como evidência. IPTU/periodicidade são omitidos quando `publishIptu=false`. Custo desconhecido permanece nulo e zero informado permanece zero. Ofertas rascunho, sem autorização ou com autorização vencida não entram. Ofertas históricas vencidas podem sustentar uma recomendação de revalidação e recebem evidência `IsCurrent=false`.

O snapshot mantém `PublicLocation` e acrescenta `Location` pública tipada, produzidas pelo mesmo helper puro da API e do consumer. A cópia standalone `CrmAi.Domain/RealEstateLocationPolicy.cs` mantém o namespace compartilhado e não introduz dependência de runtime ou de build no repositório da API. `bash tests/verify-real-estate-location-parity.sh` confere a identidade dos arquivos. As consultas desta versão requerem também a migration `322_real_estate_structured_location.sql`. Links de mapa têm origem/caminho fixos e usam apenas a projeção já filtrada, sem consulta a mapas, geocodificação ou serviços de CEP/IBGE. Conteúdo editorial público e mensagens não recebem redação heurística de endereços.

Verificação da localização em 16/10/2026: **23 testes `RealEstateAnalysis`, 0 falhas e 0 ignorados**, com PostgreSQL 18 local descartável na porta `55466`. Os dois casos novos verificam as 12 combinações de precisão global/imóvel na consulta SQL e no input analítico e a retenção de localização exata sem confirmação de carteira. A fixture sintética foi alinhada à FK empresarial da migration `320`; o helper compartilhado passou na comparação byte a byte. Provedores e canais reais não foram chamados.

A extensão comercial P1 exige ainda a migration `323_real_estate_commercial_registration.sql`. O snapshot tipado acrescenta subtipo, áreas privativa/total/terreno e legada, quartos, suítes, vagas, conservação, ocupação e características canônicas. A procura acrescenta subtipos (até 20), mínimos de suítes/vagas e características obrigatórias. O builder recalcula `ComparableArea` pelo helper compartilhado: terreno para `land`/`rural`, privativa nos demais tipos e fallback na área legada; nunca usa área total como substituta. Características nulas/ausentes não viram `false`, e chaves fora do catálogo não entram no input. Bloco, lote, condomínio e edifício entram somente na localização pública já filtrada para `full` elegível. O script de paridade também compara `RealEstatePropertyPolicy.cs`, sem dependência de runtime no backend.

Verificação P1 em 16/10/2026: **29 testes `RealEstateAnalysis` aprovados — 17 unitários e 12 com PostgreSQL local, zero falhas e ignorados**. Os casos novos verificam os campos e critérios tipados na consulta real e no input, os caminhos de área privativa/terreno/fallback sem promover área total e características nulas/negativas sem chaves arbitrárias. A matriz de precisão foi estendida aos quatro identificadores novos. Compilação sem erros/warnings novos e paridade byte a byte dos dois helpers aprovadas; sem provedor ou canal real.

Conhecimento fica restrito ao módulo imobiliário, sem produto específico, público `customer_safe`, versão corrente publicada e vigência válida. A consulta usa busca textual PostgreSQL pelo propósito da procura; não inclui a base editorial inteira.

O schema E1 não dispõe de feedback de visita. O snapshot informa que esse feedback não está disponível, sem tratar notas privadas como feedback nem afirmar que uma ausência foi comprovada.

## Evidências e saída

Os builders de risco e WhatsApp aplicam novamente a seleção das chaves e o limite; um snapshot de outro tenant é descartado mesmo se introduzido após o repositório. A compactação existente de reuniões preserva o bloco imobiliário.

O schema de risco acrescenta `realEstateFindings` apenas quando há contexto imobiliário. Cada achado contém tipo, resumo, recomendação e referências a evidências reais. O código descarta achados sem evidência, referências desconhecidas, mais de cinco referências por achado ou texto essencial vazio; mantém no máximo dez achados. A saída persistida incorpora as evidências com origem, revisão, momento e atualidade, sempre com `RequiresOperationalRevalidation=true`.

O log existente de invocação recebe revisão do módulo, momento do snapshot, IDs das fontes e quantidade de evidências vencidas, tanto em sucesso quanto em erro. Modelo, guardas, créditos e armazenamento atuais são reutilizados. Não foi introduzido cache nem outro serviço analítico. A recomendação é consultiva: disponibilidade, agenda, concessões e aceite continuam exigindo validação operacional na ferramenta responsável.

Totais de relatórios pertencem ao SQL/serviço de relatórios do backend. O contexto e o modelo não calculam totais oficiais, comissões ou receita a partir do valor de imóveis.

## Evento operacional opt-in

`RabbitMQ:RealEstateAnalysisEventsEnabled` é `false` por padrão. Quando explicitamente habilitado, acrescenta `crm.events.opportunity.real_estate.updated` às assinaturas da fila analítica existente. Não altera assinaturas de metas/check-in ou gamificação. O produtor usa `IOpportunityEventPublisher` depois do commit, causa/recurso/revisão, eventId estável e `Data.schemaVersion=1`; não publica quando não existe oportunidade.

O pipeline rejeita versões desconhecidas e só agenda a análise desse evento quando o repositório conseguiu carregar contexto imobiliário autorizado. Isso exige módulo/regras e chaves opt-in do agente. A deduplicação e o debounce de risco atuais são reutilizados, sem serviço por segmento ou chamada síncrona ao atendimento. Falhas de publicação depois do commit exigem observação/reconciliação no produtor; não se afirma entrega transacional junto à escrita.

## Verificações

Comando usado para compilação/testes, evitando processos de MSBuild concorrentes e compilação compartilhada:

```sh
dotnet test tests/CrmAi.Tests/CrmAi.Tests.csproj --no-restore --nologo -m:1 -nr:false -p:UseSharedCompilation=false
```

Suíte final: **333 aprovados, 4 ignorados, zero falhas**, total 337, em 25 segundos; compilação sem avisos/erros. Os quatro ignorados são os testes existentes de integração RabbitMQ, porque não foi configurado broker descartável. Nenhuma chamada real de IA foi feita.

PostgreSQL 18 local descartável em `127.0.0.1:55464`, bancos `crm_ai_real_estate_test`, `risk_delay_test` e `ai_invocation_guard_test`. As novas fixtures criam schemas aleatórios, aplicam a migration real `320_real_estate_foundation.sql` do backend e removem os schemas ao terminar. Exigem host local e banco com sufixo `_test`; não há fallback para configuração da aplicação.

Variáveis explícitas usadas: `REAL_ESTATE_ANALYSIS_TEST_DB`, `REAL_ESTATE_SCHEMA_SCRIPT`, `RISK_ANALYSIS_TEST_DATABASE`, `AI_INVOCATION_GUARD_TEST_DATABASE`, `CONVERSATION_ANALYSIS_TEST_DATABASE` e `SUGGESTION_VERIFICATION_TEST_DATABASE`.

```sh
REAL_ESTATE_ANALYSIS_TEST_DB='Host=127.0.0.1;Port=55464;Database=crm_ai_real_estate_test;Username=<papel-local>' \
REAL_ESTATE_SCHEMA_SCRIPT='../BACKEND_CRM_SAAS/src/CrmRestApi.Infrastructure/Persistence/scripts/schema/320_real_estate_foundation.sql' \
dotnet test tests/CrmAi.Tests/CrmAi.Tests.csproj --no-restore --nologo -m:1 -nr:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~RealEstateAnalysis|FullyQualifiedName~ScheduledRiskAnalysisProcessorTests'
```

Cobertura nova: T23 (eventos/contextos antigos, chaves independentes), T03 (envelope/payload/lease e vínculos armazenados de WhatsApp/Instagram de outro tenant antes dos filhos), T05/T06 (projeção, IPTU, rascunhos, público, vigência), T10 (procura preservada sem oferta), limites reais de materialização SQL e de texto, evidências vencidas, cronologia própria da qualificação, achados sem prova, schema condicional, preservação na compactação, assinatura de evento imobiliário opt-in/versão e logs de sucesso/erro com provedor simulado.

A primeira execução conjunta das suítes antigas revelou colisão de fixtures: `AiInvocationGuardTests` cria `whatsapp_conversations` mínima, enquanto `ConversationAnalysisDeferralStoreTests` assume `last_analysis_status`, `last_analysis_at` e `updated_at`. As três colunas foram completadas somente no banco descartável; os arquivos preexistentes foram preservados. A execução final acima passou depois dessa preparação.

Não foram avaliados qualidade do modelo real, custo/latência de produção, benchmark de volume representativo ou integração com RabbitMQ. Os testes novos medem seleção, orçamento e invariantes com dados sintéticos; não comprovam integração operacional completa.
