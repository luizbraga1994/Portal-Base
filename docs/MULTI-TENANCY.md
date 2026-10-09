# Multi-Tenancy, Provisionamento, Roteamento e Isolamento (Prompt 03)

Este documento descreve como o Portal Base **isola fisicamente** cada
contribuinte (um banco por tenant), como o banco correto é **resolvido no
servidor** a partir do usuário autenticado (nunca do cliente) e como novas
bases são **provisionadas** com credenciais e grants restritos.

> Regra soberana (seção 37): um usuário autorizado ao **Tenant A** não pode,
> alterando qualquer identificador sob controle do cliente (URL, query, form,
> payload, cookie, estado Blazor/SignalR), acessar o **Tenant B**. Isto é
> provado por testes contra MySQL real (ver [Testes de isolamento](#testes-de-isolamento)).

## Princípio

```
1 banco de catálogo central   +   1 banco isolado por contribuinte (o tenant É a base).
```

- **Sem** `TenantId` em tabelas de negócio, **sem** schema compartilhado, **sem**
  Row-Level Security, **sem** banco compartilhado entre contribuintes.
- **Fail-closed:** sem tenant resolvido e autorizado, **nenhum** `DbContext`
  de tenant é aberto. Não há tenant default, banco de dev, fallback silencioso
  nem conexão global para dado de contribuinte.

## Pipeline de resolução (servidor)

O identificador do tenant **nunca** é aceito do cliente. Ele é resolvido a
partir do **principal autenticado** contra o catálogo confiável:

```
Usuário
  │  (identidade estabelecida pela camada de autenticação — Prompt 04)
  ▼
Autenticação  ──►  principalId (nunca o TenantId vindo do cliente)
  ▼
Autorização (IAutorizacaoTenant, contra o catálogo)
  │   • usuário ATIVO?      • tenant PRONTO (operacional)?
  │   • vínculo usuário×tenant ATIVO? (deny-by-default)
  ▼
TenantContext (ITenantContext)         estados: NaoResolvido → Resolvido → Autorizado | Invalido
  │   exige EstaAutorizado == true
  ▼
TenantDatabaseResolver
  (ConstrutorConexaoTenant + IProvedorSegredos)
  │   • NomeBaseFisica derivado do catálogo (nunca do cliente), validado por regex
  │   • credencial de RUNTIME resolvida do cofre (nunca em log/exceção/HTTP)
  ▼
TenantDbContext (IFabricaContextoTenant.CriarAsync)
  │   • recusa se o contexto não estiver Autorizado (fail-closed)
  ▼
Banco isolado do contribuinte  (portalbase_tenant_<nome>; legado: tenant_<guid:N>)
```

Qualquer etapa que falhe **interrompe** o fluxo com falha segura: o contexto
permanece não-autorizado e a fábrica recusa abrir conexão.

### Deny-by-default e troca explícita de tenant

- `ContextoTenant` (Scoped) nasce em `NaoResolvido`. Nada é acessível até uma
  resolução bem-sucedida.
- `ResolvedorTenant.ResolverAsync` **reinicia** o contexto antes de resolver
  (nunca reaproveita o "último tenant") e só marca `Autorizado` após checar o
  vínculo no catálogo; caso contrário marca `Invalido` e lança
  `TenantNaoAutorizadoException`.
- O `NomeBaseFisica` só é exposto quando o estado é `Autorizado`.

## Nome do tenant e nome da base

Cada tenant tem um **nome escolhido na criação** (`NomeTenant`, ex.: `STAKE`) — operação
administrativa (caso de uso `CriarTenant`, autorizado por `plataforma.tenant.provisionar`, ou o
comando de acesso local em Development). O nome:

- é **sempre MAIÚSCULO** (normalizado: `stake` → `STAKE`) e é o que aparece na seleção de
  contribuinte e no topo do portal;
- usa só letras sem acento, dígitos e `_`, começando por letra, de 2 a 40 caracteres
  (`^[A-Z][A-Z0-9_]{1,39}$`) — esse alfabeto restrito é o que o torna seguro como identificador de
  banco;
- é **único** no catálogo (verificação no caso de uso + índice `ux_tenant_nome` contra corrida) e
  **imutável** depois de criado (define a base física);
- define a base: `NomeBaseFisicaTenant.Derivar` produz `portalbase_tenant_<nome em minúsculas>`
  (`STAKE` → `portalbase_tenant_stake`; 18 + 40 ≤ 64, o limite do MySQL).

O nome físico é **gravado no catálogo** na criação e sempre **lido de lá** — nunca vem da requisição
em tempo de uso (o cliente só envia o `TenantId`, que o servidor confere contra o vínculo). O padrão
legado `tenant_<guid:N>` (nome derivado do Id, anterior ao nome escolhido) continua aceito, por
compatibilidade, para a base do próprio tenant.

Antes de qualquer DDL, `ConstrutorConexaoTenant.ValidarNomeBase` revalida o padrão
`^[a-z][a-z0-9_]{1,63}$` **e** rejeita esquemas de sistema reservados (`mysql`,
`information_schema`, `performance_schema`, `sys`). Nomes manipulados
(`"tenant_a; DROP DATABASE x"`, backtick, maiúsculas, `../escape`) nunca chegam ao DDL.

### Base que já existe (nunca herdar dados de outro contribuinte)

Com o nome escolhido, a base pode existir **antes** do tenant (sobra de um catálogo perdido,
criação manual). Cada base recebe, no provisionamento e **antes** das migrations, uma marca de
proprietário (tabela de uma linha `portalbase_proprietario`: `tenant_id` + `cnpj`). No
provisionamento inicial de uma base que já existe, `ReaproveitamentoBaseTenant` só aceita (fail-closed):

| Situação da base existente | Resultado |
|---|---|
| vazia (nenhuma tabela) | reaproveitada (auditado `TenantDatabaseReused`) |
| marcada com o **próprio** tenant (nova tentativa) | reaproveitada |
| marcada com o **mesmo CNPJ** (o contribuinte recadastrado) | reaproveitada, a marca passa ao novo tenant (auditado `TenantDatabaseReused`) |
| marcada com **outro** contribuinte | **recusada**: tenant `Falhado`, nada migrado nem concedido, auditado `TenantDatabaseOwnershipConflict` |
| com tabelas e **sem** marca | **recusada** (origem desconhecida), exceto o nome legado `tenant_<id>` do próprio tenant |

O comando de acesso local aplica a mesma regra **antes de gravar** (o nome é imutável: um tenant
criado com um nome em conflito ficaria preso).

## Provisionamento

Estados (`EstadoProvisionamento`): `Pendente → Provisionando → Pronto`, com
`Falhado` e `Desabilitado`. Orquestrado por `ProvisionadorTenant`:

1. **Lock distribuído** (`ILockProvisionamento`, `GET_LOCK`/`RELEASE_LOCK` do
   MySQL) por tenant: dois provisionamentos concorrentes não corrompem — um
   efetiva, o outro observa `Provisionando` e sai.
2. **Idempotente:** tenant já `Pronto` apenas reconcilia schema (não recria a
   base); `Desabilitado` é ignorado; `Pendente`/`Falhado` provisiona.
3. Cria a base se ausente (`IAdministradorBancoTenant`, DDL com identificadores
   validados), aplica migrations (`IMigradorTenant`, credencial **admin**),
   concede grants restritos, registra `SchemaVersion`/`UltimaMigracao` e marca
   `Pronto`. Falha na migration deixa `Falhado` (não-operacional).
4. Erros são **sanitizados** (sem connection string, sem stack trace) antes de
   virarem `ResultadoProvisionamento.Erro` ou log.

O `IRunnerMigracoesTenant` aplica/atualiza migrations por tenant e o
`IDiagnosticoTenant` detecta divergências catálogo↔banco (base ausente para
tenant `Pronto`; banco órfão sem tenant no catálogo).

## Credenciais e grants

Duas credenciais separadas, ambas resolvidas do **cofre** (`IProvedorSegredos`,
por referência — nunca a credencial em código/`appsettings`/log):

| Credencial | Uso | Escopo |
|---|---|---|
| **Admin** | DDL: `CREATE DATABASE`, migrations, `GRANT`, lock | Servidor (provisionamento) |
| **Runtime** | DML da aplicação (abertura do `TenantDbContext`) | `SELECT,INSERT,UPDATE,DELETE` **por base**, **nunca** `*.*` |

O grant de runtime é concedido **por base** (`GRANT … ON \`portalbase_tenant_stake\`.* TO …`),
nunca global. Prova: a credencial de runtime **não** consegue abrir o banco de
catálogo (teste `Runtime_Nao_Acessa_O_Catalogo`).

## Pool e ciclo de vida

- Pool configurado por conexão de tenant (`OpcoesBancoTenant`:
  `MaximoPool`, `MinimoPool`, `VidaConexaoSegundos`, `TimeoutConexaoSegundos`).
- `ContextoTenant` é **Scoped** (uma instância por requisição/escopo — nunca
  singleton/global). O `PortalBaseTenantDbContext` **não** é registrado em DI:
  só a `IFabricaContextoTenant` fail-closed o cria, a partir de um contexto
  autorizado, e é descartado com o escopo.

## Worker com tenant explícito

Jobs de segundo plano executam **sempre** sob um tenant explícito via
`IExecutorTarefaTenant`: ele resolve/autoriza o tenant por operação (fail-closed)
e nunca herda um "tenant ambiente". Sem tenant autorizado, a tarefa não roda.

## Testes de isolamento

Contra **MySQL real** (`PORTALBASE_TEST_MYSQL_ADMIN`), a suíte
`TenantIsolationTests` tenta deliberadamente quebrar o isolamento:

| Cenário | O que prova |
|---|---|
| `Dado_De_A_Nao_Aparece_Em_B` | Dado gravado em A não existe em B; bases físicas distintas (TESTE 1/2/9). |
| `Usuario_De_A_Nao_Resolve_B` | **IDOR**: usuário só de A, ao pedir B, é negado (TESTE 3). |
| `Resolucao_Negada_Nao_Abre_Conexao_De_Tenant` | Resolução negada deixa o contexto não-autorizado e a fábrica recusa (TESTE 4). |
| `Fabrica_Sem_Contexto_Autorizado_E_Rejeitada` | Fábrica sem contexto autorizado é rejeitada (fail-closed, TESTE 5). |
| `Tenant_Desabilitado_E_Negado` | Tenant desabilitado bloqueia o acesso (TESTE 6). |
| `Provisionamento_Concorrente_Nao_Corrompe` | Dois provisionamentos simultâneos: sem corrupção; um efetiva (TESTE 7). |
| `Runtime_Nao_Acessa_O_Catalogo` | Grant por base: runtime não lê o catálogo (CA-012). |
| `Nome_De_Base_Manipulado_E_Rejeitado` | Identificadores manipulados/ reservados não vão para DDL (TESTE 10). |

A resolução usa apenas `principalId` (autenticado) + `tenantId` interno; não há
caminho pelo qual um valor do cliente selecione a base — é isto que fecha a
seção 37.

## Regras transversais atendidas (mapa)

Fail-closed (5/6), tenant não vem do cliente (4), autorização no servidor (3/15),
sem SQL concatenado (13), segredos só no cofre (7), sem segredo/PII/connection
string em log/exceção/HTTP (8/16), auditoria de operação crítica (9), validação
de fronteira (10), timeout/`CancellationToken` em integração (11), idempotência
(12), sem `IgnoreQueryFilters` para contornar segurança (14), testes conforme o
risco (19), build+testes ao final (20).
