# Provisionamento Operacional e Ciclo de Vida de Tenants (Prompt 09)

Consolida o ciclo de vida completo de um contribuinte (tenant), do cadastro à operação, de forma
**segura, idempotente, auditável, concorrente, fail-closed e isolada por tenant**. Complementa a
fundação multi-tenant do [`docs/MULTI-TENANCY.md`](MULTI-TENANCY.md) (Prompt 03).

## Estados e transições

Máquina de estados server-side ([`EstadoProvisionamento`](../src/PortalBase.Domain/Catalogo/EstadoProvisionamento.cs)),
encapsulada no agregado [`Tenant`](../src/PortalBase.Domain/Catalogo/Tenant.cs). O cliente **nunca**
define o estado — não existe `status=Active` vindo da requisição.

| Estado | Significado |
|---|---|
| `Pendente` | Registrado no catálogo; base ainda não provisionada. |
| `Provisionando` | Criação de base, migrations e grants em andamento. |
| `Pronto` (Ready) | Infraestrutura criada **e validada** — porém **ainda não operacional**. |
| `Falhado` | Falha de provisionamento; não operacional até reprocessar. |
| `Ativo` (Active) | **Único** estado operacional (autoriza a operação funcional sobre os dados do tenant). |
| `Desabilitado` | Bloqueado para toda operação (base preservada). |

```
Pendente ─► Provisionando ─► Pronto ─► Ativo ─► Desabilitado ─► Ativo
                 │                                  ▲
                 └─► Falhado ─► Provisionando        (reativação)
```

- **Ready ≠ Active** (seção 4): `Pronto` é infraestrutura pronta e validada; `Ativo` é a
  autorização funcional de uso. `Tenant.EstaOperacional` ⟺ `Ativo`.
- Transições: ativação `Pronto→Ativo` ([`Tenant.Ativar`]), desabilitação `Ativo/Pronto→Desabilitado`
  ([`Tenant.Desabilitar`]), reativação `Desabilitado→Ativo` ([`Tenant.Reativar`]), reprocesso
  `Falhado→Provisionando`. Transições inválidas lançam `EstadoInvalidoException` (fail-closed).

## Provisionamento

[`ProvisionadorTenant`](../src/PortalBase.Application/Provisionamento/ProvisionadorTenant.cs),
idempotente e fail-closed, sob **lock** por tenant (seção 6):

1. Adquire o lock (`GET_LOCK` do MySQL); se outro processo já provisiona, não inicia concorrente.
2. `Pendente/Falhado` → `Provisionando`; `Pronto/Ativo` → atualização de schema (preserva o estado).
3. Cria a base **se ausente** (`CREATE DATABASE IF NOT EXISTS`) — idempotente; audita `TenantDatabaseCreated` só quando cria.
4. Aplica migrations (`TenantDatabaseMigrationStarted/Completed`).
5. Concede grants mínimos ao usuário de **runtime**.
6. **Valida** (seção 15) antes de marcar Ready — ver abaixo.
7. `ConcluirProvisionamento` → `Pronto`. Falha em qualquer etapa → `Falhado` (nunca "Ready parcial").

O provisionamento termina em **Ready**; a **ativação** (Ready→Active) é um passo separado,
autorizado, que torna o tenant operacional.

### Validação pós-provisionamento (Ready = validado)

"O banco existe" não basta (seção 4). [`ValidadorProvisionamentoTenant`](../src/PortalBase.Infrastructure/Provisionamento/ValidadorProvisionamentoTenant.cs)
confere, com **CancellationToken**: sem migrations pendentes, **tabelas essenciais** presentes
(checagem parametrizada em `information_schema`), e **conectividade com a credencial de runtime**.
Qualquer falha ⇒ inválido ⇒ o provisionamento vira `Falhado` e a ativação é recusada.

## Ciclo de vida operacional

[`CicloVidaTenant`](../src/PortalBase.Application/Provisionamento/CicloVidaTenant.cs) — ativar,
desabilitar, reativar. Toda operação é:

- **Autorizada no servidor** (seção 20): permissão administrativa `admin.tenant` **no tenant**,
  deny-by-default, via `IServicoAutorizacao.ExigirAdministrativoAsync`. A autorização
  administrativa NÃO exige tenant operacional (o ciclo de vida age justamente sobre tenants ainda
  não operacionais), mas exige usuário ativo + vínculo ativo.
- **Validada** na ativação e reativação (revalida a infraestrutura antes de tornar operacional).
- **Idempotente** (ativar um tenant já ativo, desabilitar um já desabilitado → no-op).
- **Auditada**: `TenantActivated`, `TenantDisabled`, `TenantReactivated`.

**Desabilitação** (seção 21): um tenant `Desabilitado` não resolve contexto, não autoriza acesso
funcional, não permite upload nem operações. A **base NÃO é removida** (seção 23 — sem `DROP`
automático). **Reativação** (seção 22): revalida (base existe, conecta, schema íntegro) antes de
`Disabled→Active`.

## Segurança

- **Nome da base derivado do nome do tenant** (seção 8): `NomeBaseFisicaTenant.Derivar` produz
  `portalbase_tenant_<nome>` a partir do `NomeTenant` escolhido na criação (alfabeto restrito, único,
  imutável), nunca de razão social/e-mail; o nome físico é gravado no catálogo e lido de lá. O CNPJ é
  validado/normalizado, mas **não** é autoridade de isolamento (o `TenantId` é).
- **Base pré-existente nunca é herdada de outro contribuinte**: a base recebe a marca de proprietário
  (`portalbase_proprietario`: tenant + CNPJ) antes das migrations; no provisionamento inicial de uma base
  que já existe, só a vazia, a do próprio tenant ou a do mesmo CNPJ é reaproveitada — senão o tenant fica
  `Falhado` sem migrar nem conceder nada (`TenantDatabaseOwnershipConflict`). Detalhes em
  [MULTI-TENANCY.md](MULTI-TENANCY.md#base-que-já-existe-nunca-herdar-dados-de-outro-contribuinte).
- **Identificadores validados** (seções 7, 31): todo nome de base passa por `ValidarNomeBase`
  (regex restrito + esquemas reservados bloqueados) antes de compor DDL — identificadores não são
  parametrizáveis em SQL. Nomes manipulados (`; DROP`, `../`, backticks) são rejeitados.
- **Credenciais separadas** (seções 9, 10, 11): a credencial de **runtime** recebe apenas DML na
  base do tenant (nunca `*.*`); DDL/grants usam a credencial **administrativa**, separada. `root`
  nunca é a credencial da aplicação. A connection string final existe só em memória — **nunca** em
  log, auditoria, UI ou catálogo; senhas ficam no cofre por referência (Prompt 06).
- **Autorização server-side, tenant-scoped, deny-by-default**; o `TenantContext` permanece a fonte
  de autoridade (seção 26): autenticação → tenant selecionado → autorização → estado → base.
- **Concorrência** (seção 32): `GET_LOCK` no provisionamento; concorrência otimista
  (`ConcurrencyStamp`) nas transições de estado — o estado final é sempre consistente.

## Failure / Recovery

- Falha de migration/validação ⇒ `Falhado`; a base **não** é apagada (pode conter evidências —
  seção 16). Reprocessar é `Falhado→Provisionando` (idempotente).
- **Retry** (seção 17): a idempotência permite reexecutar com segurança; operações não idempotentes
  (`CREATE DATABASE`) são protegidas por `IF NOT EXISTS` + checagem de existência.
- **Cancelamento** (seção 37): `CancellationToken` em todo o fluxo; cancelar não marca Active nem
  corrompe o estado.

## Auditoria e métricas

- Eventos (seção 18): `TenantProvisioningStarted/Completed/Failed`, `TenantDatabaseCreated`,
  `TenantDatabaseMigrationStarted/Completed/Failed`, `TenantActivated`, `TenantDisabled`,
  `TenantReactivated` — com tenant, ator, resultado, correlação e motivo sanitizado. **Nunca**
  senha/connection string/segredo.
- Métricas (seção 33): [`MetricasProvisionamento`](../src/PortalBase.Infrastructure/Provisionamento/MetricasProvisionamento.cs)
  via `Meter` (`provisioning_started/success/failure`, `provisioning_duration`,
  `provisioning_migration_duration`), exportáveis por OpenTelemetry. Sem dados sensíveis.

## O que NÃO faz parte do provisionamento

Deleção física de tenant (`DROP DATABASE`) automática e criação de usuário inicial `admin/admin`
(o primeiro acesso em Development é criado pelo comando de acesso local — ver
[AMBIENTE-LOCAL.md](AMBIENTE-LOCAL.md)). A desabilitação preserva a base; o encerramento
contratual e a retenção ficam a cargo do domínio de quem adota o template.
