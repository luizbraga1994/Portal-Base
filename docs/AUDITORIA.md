# Auditoria, Logs, Rastreabilidade e Retenção (Prompt 05)

Como o Portal Base registra **quem fez o quê, quando, em qual contribuinte e com qual
resultado**, correlaciona operações ponta a ponta, protege a trilha contra adulteração e
retém os dados pelos prazos legais — sempre com **decisão de acesso no servidor** e sem
transformar a trilha em cópia de dados sensíveis.

## Três conceitos distintos (não misturar)

| | Auditoria | Log operacional | Telemetria |
|---|---|---|---|
| Para quê | comprovar quem/quando/tenant/ação/recurso/resultado | diagnóstico, troubleshooting, exceções | métricas, traces, duração, dependências |
| Onde | tabela `registro_auditoria` (catálogo), append-only | Serilog estruturado (console/OTLP) | OpenTelemetry |
| Estrutura | registro tipado, campos fixos + integridade | template estruturado + propriedades | spans/metrics |

Um log técnico **não** substitui um registro de auditoria; a auditoria **não** depende de
texto livre.

## Modelo de auditoria

`EventoAuditoria` (Application) → `IAuditoria` (porta) → `AuditoriaEfCore` (Infra) →
`registro_auditoria`. Campos: `Id`, `Tenant`, `PrincipalId`, `TipoAtor`, `Acao`,
`TipoRecurso`/`RecursoId`, `Resultado`, `Motivo`, `CorrelationId`, `TraceId`, `IpAddress`,
`UserAgent`, `Origem`, `Job`, `Competencia`, `Protocolo`, `Categoria` (retenção),
`ValoresAnteriores`/`ValoresNovos`/`Metadados` (JSON sanitizado), `OcorridoEmUtc`,
`IntegridadeHash`. `Competencia` (período de referência, `AAAA-MM`) e `Protocolo`
(identificador externo) são **opcionais** e genéricos: o domínio do seu portal os preenche quando
houver um período ou um número de protocolo a correlacionar; o template não os usa.

**ActorType** (`TipoAtor`): `Usuario`, `Sistema`, `JobBackground`, `Integracao` — uma operação
de sistema nunca é atribuída a um usuário inexistente. O tipo vem do contexto de execução
(Web = Usuário; Worker = JobBackground).

## Eventos (catálogo `AcaoAuditoria`)

Nomes estáveis e pesquisáveis, cobrindo Identidade (`LoginSucceeded`, `LoginFailed`,
`AccountLocked`, `MFAValidated`, `MFARecoveryUsed`, `SessionRevoked`, …), Autorização
(`AuthorizationDenied`, `AccessGranted/Revoked`, `RoleAssigned/Removed`, `RoleCreated/Updated/Deleted`,
`PrivilegeEscalationAttempt`, `TenantAccessDenied`, `PlataformaAcessoNegado`), Tenant
(`TenantContextSelected/Switched`, `TenantProvisioning*`, `TenantDatabaseMigration*`,
`TenantDatabaseReused`), Administração (`UserCreated/Updated`, `TenantUserLinked/Unlinked`,
`PlatformAdminGranted/Revoked`), Cofre (`SecretAccessed`, `SecretUpdated`), Uploads (`Upload*`),
Backup/Restore (`Backup*`, `Restore*`), Incidentes (`Incidente*`), Marca, E-mail e operações
(`AlertaOperacionalDisparado`, …). Ao acrescentar operações críticas do seu domínio, crie as ações
correspondentes em `AcaoAuditoria` — nunca strings soltas.

## Old/New e sanitização

`ValoresAnteriores`/`ValoresNovos` recebem apenas uma **allowlist** de propriedades (ex.:
papel anterior/novo, status anterior/novo) — nunca o objeto inteiro. `SanitizacaoAuditoria`
(regra única, centralizada) **redige** valores de chaves sensíveis (senha, hash, token,
segredo, MFA/OTP, recovery, cookie, authorization, connection string, PFX, private key,
client secret, certificado) e trunca valores longos, tanto em dicionários quanto em texto
livre. Assim, **nunca** entram na trilha: senha, hash de senha, segredo TOTP, códigos de
recuperação, tokens, client secret, chave privada, PFX, connection string ou credenciais.

## Rastreabilidade e correlação

Um `IContextoAuditoria` (escopo de requisição/execução) carrega `CorrelationId`, `TraceId`,
`IpAddress`, `UserAgent`, `Origem`, `Job` e `TipoAtor`. O host preenche uma vez e o **sink**
enriquece todo evento — os casos de uso não repetem correlação. No **Web**, um middleware gera/
propaga o correlation id (cabeçalho `X-Correlation-ID`), captura o trace id e o IP e alimenta o
log estruturado e a auditoria. No **Worker**, o `ExecutorTarefaTenant` define
`TipoAtor=JobBackground`, o nome do job e um correlation id por execução — e **resolve o tenant
explicitamente** (nunca herda nem assume tenant; seção 10). IP só é confiado de proxies
configurados em `Rede:ProxiesConfiaveis` (senão usa o IP da conexão direta; seção 23).

## Integridade / tamper-evidence

Cada registro carrega um **HMAC-SHA256** (`IntegridadeHash`) sobre seus campos canônicos, com
chave no **cofre** (`Auditoria:ChaveIntegridadeRef` → segredo Base64; nunca no código). Sem a
chave não é possível recomputar o hash, então **modificar qualquer campo torna-se detectável**.
O timestamp é canonizado de forma estável ao roundtrip do banco (Unix segundos, independente de
`Kind`/precisão).

> Isto fornece **tamper-evidence em nível de aplicação/banco** (detecção de modificação). Não é,
> por si só, garantia jurídica de imutabilidade absoluta. A **não-remoção** é reforçada por
> append-only + grants (abaixo). Um hash-chain encadeado é endurecimento futuro.

## Append-only e grants

A aplicação **apenas insere** na trilha — não há caminho de `UPDATE`/`DELETE` no código, e a
consulta é somente leitura. No banco, a credencial de runtime da aplicação deve ter **apenas
`SELECT, INSERT`** em `registro_auditoria` (sem `UPDATE`/`DELETE`); operações de manutenção
usam credencial administrativa separada. Um teste real prova que uma credencial `SELECT,INSERT`
recebe **acesso negado** ao tentar `DELETE`/`UPDATE`.

## Transações (estratégia — seção 9)

O registro de auditoria é **auto-committado** (append durável). Como compartilha o mesmo
`DbContext` de escopo, nas mutações críticas ele é gravado logo após o commit de negócio, na
mesma requisição. Persistir na **mesma transação** de negócio (ou numa fila transacional gravada
junto com ela) é endurecimento futuro (seção 9/32). Janela residual (negócio commitado, auditoria
falha) é pequena e documentada.

## Consulta autorizada e isolada

`ConsultaAuditoriaAutorizada` exige a permissão `auditoria.visualizar` **no tenant solicitado**
(server-side; `?tenantId=B` nunca é bypass), **força** o filtro ao tenant autorizado, **pagina
obrigatoriamente** (máx. 200/página) e **audita o próprio acesso** (`AuditQueried` — evento de
escrita, sem recursão). Por padrão só o **Administrador** tem a permissão (Leitor/Analista não).
Acesso cross-tenant/sistema é explicitamente **fora de escopo** aqui e exigirá autorização separada
no futuro (por isso eventos de identidade em escopo `identidade` não aparecem na consulta por
contribuinte).

**Nome do ator (exibição).** Depois de autorizada, a consulta traduz os `PrincipalId` dos atores
do tipo usuário da página em **nome e login** (`IConsultaNomesAtores`, sobre o catálogo; só
id/nome/login, nunca dados da conta) — o registro gravado não muda: o identificador continua
sendo a referência da trilha (a tela o mostra na dica do nome). O filtro **Ator** aceita o
identificador ou nome/login/e-mail (sem diferenciar maiúsculas nem acentos, `LIKE` parametrizado
com curingas escapados, no máximo 50 usuários); a lista de identificadores resultante é montada
**só no servidor** (`FiltroAuditoria.Atores` vindo do cliente é descartado) e a consulta continua
presa ao tenant autorizado.

## Índices (consultas comuns)

`(Tenant, OcorridoEmUtc)`, `(PrincipalId, OcorridoEmUtc)`, `(Acao, OcorridoEmUtc)`,
`CorrelationId`, `TraceId`, `(TipoRecurso, RecursoId)`, `(Tenant, Protocolo)`,
`(Tenant, Competencia)`. Sem índice em payload grande (JSON/text).

## Retenção

`IPoliticaRetencao` mapeia a **categoria** de cada registro ao prazo legal e diz se está dentro
da janela — a retenção **identifica** a obrigação, não apaga automaticamente ao expirar.

| Categoria | Retenção | Implementação |
|---|---|---|
| Trilha de auditoria | 10 anos | `CategoriaRetencao.Auditoria` (padrão dos eventos) |
| Arquivos de importação | 5 anos | `CategoriaRetencao.ArquivosImportacao` |

Acrescente em `CategoriaRetencao` (e em `IPoliticaRetencao`) as categorias e os prazos legais do
seu domínio.

## Relógio

Todas as datas são **UTC**, via `IRelogio` (nunca `DateTime.Now`) — o que também torna
determinísticos os testes de expiração/retenção/janela.

## Limitações conhecidas

- Correlação de auditoria em componentes **Blazor interativos** (circuito SignalR) usa o
  contexto do request inicial; operações disparadas por endpoints (login/MFA/logout) e por jobs
  são totalmente correlacionadas. Enriquecimento por circuito é evolução futura.
- Integridade é por-registro (HMAC), não encadeada; a não-remoção depende dos grants append-only.
- Consulta é por-tenant; visão cross-tenant/sistema requer autorização separada (futuro).
- **Compliance legal** não é declarado por ter-se implementado um controle técnico.
