# Como usar o template

Este repositório é a base de um portal multi-tenant. Ele já traz identidade, autorização, auditoria, cofre,
arquivos, backup, observabilidade, Worker e a casca da tela. O **domínio** do portal novo é você que acrescenta —
este guia mostra onde, na ordem certa, sem quebrar os guardrails de [`CLAUDE.md`](../CLAUDE.md).

## 1. Criar o portal

```bash
dotnet new install /caminho/do/Portal-Base        # uma vez por máquina
dotnet new portalbase -n PortalTributos --nomeExibicao "Portal Tributos" --organizacao "Minha Empresa" -o PortalTributos
cd PortalTributos
git init && git add . && git commit -m "Portal Tributos a partir do Portal Base"
```

| Parâmetro | Troca | Exemplo |
|---|---|---|
| `-n` | `PortalBase` (projetos, namespaces, arquivos), `portalbase` (bases `portalbase_*`, segredos `portalbase-*`, cookies, `portalbase.css/js`), `PORTALBASE` (variáveis de ambiente) e `portalBase` (objeto JavaScript) | `PortalTributos` → `portaltributos_tenant_acme` |
| `--nomeExibicao` | “Portal Base” nas telas, e-mails e documentos | `"Portal Tributos"` |
| `--organizacao` | “Andersen Consulting” na tela de entrada, e-mails e rodapé | `"Minha Empresa"` |

**Nome (`-n`): até 16 caracteres, só letras e dígitos, começando por letra** (ex.: `PortalTributos`,
`PortalFinanceiro`). Ele vira prefixo de identificadores do MySQL com limite de tamanho — a base do contribuinte
`<nome>_tenant_<tenant de até 40>` (máx. 64) e as contas `<nome>_app`, `<nome>_admin`, `<nome>_migrador` (máx. 32).
Com nome maior, o portal gerado falha nos testes de `LimitesNomePortalTests` — de propósito, antes de algum
contribuinte não conseguir ter a base criada.

Depois de criar, confira:

1. `dotnet build -warnaserror` e `dotnet test` (ver [README](../README.md#build-e-testes)).
2. As migrations iniciais (`src/*.Infrastructure/Persistencia/Catalogo/Migrations` e `.../BaseTenant/Migrations`)
   já estão no projeto. Se quiser recomeçar o histórico com o nome novo, apague as duas pastas e gere de novo
   (passo 4.3).
3. Logo e cores: **Configurações → Marca / Logo** (pela tela) e `src/*.Web/wwwroot/css/portalbase.css`.

## 2. O que é “tenant” aqui

Tenant = **contribuinte** (a empresa cliente). O catálogo central guarda só o cadastro (CNPJ, nome, razão social),
usuários, perfis, vínculos, auditoria e metadados. **Todo dado de negócio vai para o banco do tenant**, aberto apenas
pela `IFabricaContextoTenant` a partir de um `ITenantContext` autorizado (regras 4, 5 e 6). Se o portal novo usar
outro termo (cliente, empresa, filial), troque só os textos das telas — os tipos podem continuar como estão.

## 3. Mapa do que acrescentar para uma funcionalidade nova

Exemplo: cadastro de **Pedidos** do contribuinte.

| # | Camada | Onde | O quê |
|---|---|---|---|
| 1 | Domínio | `src/*.Domain/Pedidos/Pedido.cs` | Entidade encapsulada: sem setters públicos, fábrica que valida (`EntradaInvalidaException`), invariantes no agregado, `ConcurrencyStamp` se houver edição concorrente. |
| 2 | Persistência | `src/*.Infrastructure/Persistencia/BaseTenant/Configuracoes/ConfiguracaoPedido.cs` | `IEntityTypeConfiguration<Pedido>` (tabela, colunas, índices, `HasMaxLength`, decimais `HasPrecision(19, 2)`). É aplicada sozinha: o contexto do tenant carrega tudo desse namespace. |
| 3 | Persistência | `PortalBaseTenantDbContext` | `public DbSet<Pedido> Pedidos => Set<Pedido>();` |
| 4 | Migration | ver 4.3 | Migration do **tenant**. Acrescente a tabela essencial em `ValidadorProvisionamentoTenant.TabelasEssenciais`. |
| 5 | Permissões | `Domain/Catalogo/Enums.cs`, `Application/Autorizacao/Permissoes.cs`, `CatalogoPapeisPermissoes.cs`, `Web/Ui/Dados/DadosAdministracao.cs` | Novo valor **ao final** de `PermissaoConhecida`, a chave em `Permissoes` (ex.: `pedidos.aprovar`), o mapa em `ChaveDe`, quem recebe na `Matriz`, o grupo na tela de permissões e — se for alto impacto — em `Permissoes.ExigemMfa`. Gera migration do **catálogo** (seed de permissões). |
| 6 | Aplicação | `src/*.Application/Pedidos/` | Porta (`IRepositorioPedido`), caso de uso (exige `ITenantContext` autorizado + `IServicoAutorizacao.ExigirAsync(tenantId, permissao)`, valida a entrada, grava, audita com uma constante nova em `AcaoAuditoria`), read-model paginado (`ResultadoPaginado`, `Paginacao`). |
| 7 | Infraestrutura | `src/*.Infrastructure/Pedidos/` + `ServicosInfrastructure.cs` | Repositório e consulta pelo `IFabricaContextoTenant` (nunca conexão fixa, nunca `IgnoreQueryFilters`, só LINQ/consultas parametrizadas). Registre no ponto marcado “Acrescente aqui os registros do domínio do seu portal”. |
| 8 | Tela | `src/*.Web/Ui/Dados/DadosPedidos.cs` + `ServicosUi.cs` | Adaptador de UI herdando `DadosTenantBase` (mapeia sem tenant / sem permissão / erro para estado seguro; nunca mostra detalhe interno). |
| 9 | Tela | `src/*.Web/Components/Pages/Pedidos/Pedidos.razor` | `@rendermode InterativoSemPrerender`, guarda `Visual.Pode(Permissoes.X)` só para UX, filtros + paginação no servidor, `ConfirmacaoAcao` para ações. |
| 10 | Menu | `src/*.Web/Ui/Navegacao/NavegacaoPortal.cs` | Item com a **mesma** permissão que a consulta exige no servidor. |
| 11 | Testes | `tests/` | Domínio (invariantes), Aplicação (autorização negada, tenant ausente, entrada inválida, auditoria), Integração com MySQL (isolamento A × B, migration, paginação no banco) e, para fluxos críticos, Browser E2E. |

Trabalho pesado (importação grande, integração lenta): crie um valor em `TipoTarefaSegundoPlano`, um
`IManipuladorTarefa` (veja `ManipuladorTarefaExemplo`) e registre em `ServicosTarefasWeb`. A tela só enfileira e
acompanha; o executor roda com a identidade de quem pediu.

Segredo de uma integração do tenant: acrescente um valor em `TipoSegredoTenant`, o nome em `NomeSegredo` e a
permissão em `AcessoSegredoTenant.Resolver` (veja `SegredoIntegracao`). O valor vai só para o cofre.

Integração externa: `HttpClient` nomeado com timeout, retry controlado, `CancellationToken`, allowlist de hosts
(`GuardaSsrf`) e registro de saúde (`IRegistroUltimaIntegracao`) — regra 11.

## 4. Banco de dados

### 4.1 Catálogo × tenant

- **Catálogo** (`PortalBaseCatalogoDbContext`): tenants, usuários, perfis, permissões, vínculos, auditoria,
  incidentes, backups, tarefas, e-mail, marca da plataforma. Migrations em `Persistencia/Catalogo/Migrations`.
- **Tenant** (`PortalBaseTenantDbContext`): os dados de negócio de cada contribuinte. Migrations em
  `Persistencia/BaseTenant/Migrations`, aplicadas a cada base pelo provisionamento e pela tela
  **Administração → Contribuintes → Estrutura das bases → Atualizar bases**.

### 4.2 Contas do MySQL

Três papéis: administração (cria bases/usuários/grants — só provisionamento), migradora (DDL do catálogo, só em
Development) e runtime (o portal). Senhas sempre no cofre. Ver [`MULTI-TENANCY.md`](MULTI-TENANCY.md) e
[`PROVISIONAMENTO.md`](PROVISIONAMENTO.md).

### 4.3 Gerar migrations

Na pasta `src/*.Infrastructure` (com `dotnet-ef` instalado e o projeto compilado):

```bash
dotnet ef migrations add TenantPedidos --context PortalBaseTenantDbContext \
    --output-dir Persistencia/BaseTenant/Migrations --no-build
dotnet ef migrations add CatalogoPermissoesPedidos --context PortalBaseCatalogoDbContext \
    --output-dir Persistencia/Catalogo/Migrations --no-build
```

As fábricas de design (`*DbContextFactory`) leem `PORTALBASE_TENANT_DESIGNTIME_CONNECTION` /
`PORTALBASE_DESIGNTIME_CONNECTION` só quando a ferramenta precisa de conexão; gerar a migration não precisa.

## 5. Antes de concluir qualquer tarefa

Siga [`CLAUDE.md`](../CLAUDE.md): as 8 etapas, as 10 perguntas de impacto, a auditoria da etapa 7, a Definition of
Done e — sempre — `dotnet build -warnaserror` + `dotnet test`, reportando o resultado exato.

## 6. CI (GitHub Actions)

Os workflows de `.github/workflows` vêm **desligados**: rodam só à mão (Actions → escolha o workflow → **Run
workflow**), sem gastar minutos do Actions sozinhos — nem em push, nem em PR, nem por agendamento. O *Dependency
Review* do `security.yml` só existe em PR, então fica pulado enquanto o CI estiver desligado.

Para ligar no portal novo, acrescente os gatilhos no bloco `on:` (o `ci.yml` valida cada push; os demais, os PRs):

```yaml
on:
  push:
    branches: [ main ]
  workflow_dispatch:
```

Use `pull_request: branches: [ main ]` nos workflows de PR (`browser-e2e`, `e2e-integrated`, `performance`,
`script-ambiente-windows`, `security`) e, se quiser a varredura semanal de dependências, `schedule: - cron: '27 4 * * 1'`
no `security.yml`.
