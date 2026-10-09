# Portal Base

Template de **portal multi-tenant** em .NET 10 (Blazor Server + MySQL 8.4): a base técnica e a casca da tela,
prontas para receber o domínio de um novo portal. Cada contribuinte (tenant) tem **o seu próprio banco de dados**;
o banco de **catálogo** central guarda só identidade, acesso, auditoria e metadados operacionais.

Extraído do Portal DeRE, sem nada do domínio dele: nenhuma obrigação, evento, leiaute, certificado ou integração
específica. Fica tudo o que todo portal precisa — e com os mesmos guardrails de segurança.

Regras obrigatórias de desenvolvimento: [`CLAUDE.md`](CLAUDE.md),
[`docs/REGRAS-TRANSVERSAIS-OBRIGATORIAS.md`](docs/REGRAS-TRANSVERSAIS-OBRIGATORIAS.md) e as Leis 01, 02 e 03 em `docs/`.

## O que vem pronto

| Área | O que faz |
|---|---|
| **Multi-tenancy** | Catálogo central + um banco MySQL por tenant (`portalbase_tenant_<nome>`). O tenant é resolvido **no servidor** a partir do usuário autenticado; sem `TenantContext` autorizado nada abre (fail-closed). Provisionamento da base pela tela da plataforma, com migrations, grants, lock e validação. |
| **Identidade** | Login pelo e-mail, senha (hash Identity), MFA TOTP com QR code, códigos de recuperação, anti-replay, bloqueio por tentativas, “Esqueci a senha”, senha temporária, troca de senha, sessão com revalidação. |
| **Autorização** | Perfis Leitor, Analista e Administrador; perfis personalizados por contribuinte; permissões de tenant e de **plataforma**; “Administrador da plataforma”; operações de alto impacto exigem MFA (exemplo: `dados.excluir`). Decisão sempre no servidor — o menu é só UX. |
| **Auditoria** | Trilha append-only com HMAC de integridade, nome do ator, correlação; consulta paginada e filtrada; incidentes de segurança. |
| **Segredos** | Cofre por ambiente: Azure Key Vault, cofre local do Windows (DPAPI) ou configuração local em Development. Segredo por tenant (`SegredoIntegracao`) com permissão e auditoria. Nada de segredo em código, appsettings, banco ou log. |
| **Arquivos** | Upload com conteúdo real conferido, antivírus (Windows Defender), hash, idempotência e área privada; lista em **Dados → Arquivos**. |
| **Backup/DR** | `mysqldump` cifrado (AES-GCM), cópia externa conferida, retenção, agendamento no Worker, restauração pela tela da plataforma e CLI de decifrar backup. |
| **Observabilidade** | Serilog (arquivo + Visualizador de Eventos), OpenTelemetry, health checks, batida do Worker, alertas (Worker parado, backup atrasado, disco, e-mail) por log, incidente e e-mail. |
| **E-mail** | SMTP configurado pela tela da plataforma, layout com a marca, fila com nova tentativa. |
| **Tarefas** | Fila durável de tarefas em segundo plano (com manipulador de exemplo) para tirar trabalho pesado do circuito do Blazor. |
| **Casca da tela** | Login, MFA, seleção de contribuinte, menu lateral por grupos, dashboard, usuários, perfis, permissões, tenant, contribuintes (plataforma), e-mail, marca/logo, auditoria, incidentes, saúde, workers, backups. Tema claro/escuro, responsivo. |
| **Hospedagem** | Web e Worker no IIS/serviço do Windows (`scripts/configurar-iis.ps1`), Docker (`Dockerfile.Web`, `Dockerfile.Worker`, `docker-compose.yml`), ambiente local (`scripts/configurar-ambiente-local.ps1`). |
| **Testes** | Domain, Application, Integration (MySQL real) e Browser E2E (Playwright/Chromium). |

## Estrutura

```
src/
  PortalBase.Domain          núcleo (sem dependências de infraestrutura)
  PortalBase.Application     casos de uso, portas, autorização, auditoria
  PortalBase.Infrastructure  EF Core/MySQL, cofre, e-mail, backup, armazenamento, observabilidade
  PortalBase.Web             Blazor Server (telas, endpoints de autenticação/upload/marca)
  PortalBase.Worker          serviço em segundo plano (heartbeat, backup agendado)
  PortalBase.Shared          tipos compartilhados (Resultado/Erro)
tools/PortalBase.AcessoLocal primeiro acesso local (somente Development)
tests/                       Domain, Application, Integration, BrowserE2E
scripts/                     ambiente local, IIS/serviço, cofre do Windows, CI
docs/                        regras, leis, multi-tenancy, segurança, deploy, backup/DR
```

Direção das dependências: **Domínio ← Aplicação ← Infraestrutura/Apresentação** (nunca inverter).

## Criar um portal novo

```bash
dotnet new install ./                       # na raiz deste repositório (uma vez)
dotnet new portalbase -n PortalTributos --nomeExibicao "Portal Tributos" -o ../PortalTributos
```

O nome (`-n`, **até 16 caracteres**, só letras e dígitos) troca `PortalBase` em projetos, namespaces e arquivos, e
também os prefixos técnicos (`portaltributos_tenant_*`, segredos `portaltributos-*`, variáveis `PORTALTRIBUTOS_*`),
para dois portais no mesmo servidor nunca se misturarem. Passo a passo — inclusive como acrescentar o domínio (entidade, tabela, permissão, tela,
menu e testes): [`docs/COMO-USAR-O-TEMPLATE.md`](docs/COMO-USAR-O-TEMPLATE.md).

## Rodar localmente

Requisitos: SDK .NET 10 (ver `global.json`), MySQL 8.4 e, para os testes de navegador, Chromium (Playwright).

```powershell
.\scripts\configurar-ambiente-local.ps1      # MySQL, contas, segredos de desenvolvimento (user-secrets)
.\scripts\criar-acesso-local.ps1 --cnpj 11.444.777/0001-61 --tenant DEMO --razao-social "Empresa Demo" `
    --login admin --nome "Administrador" --email admin@exemplo.com
dotnet run --project src/PortalBase.Web
```

Detalhes: [`docs/AMBIENTE-LOCAL.md`](docs/AMBIENTE-LOCAL.md). Implantação: [`docs/DEPLOY.md`](docs/DEPLOY.md).

## Build e testes

```bash
dotnet build -warnaserror
dotnet test tests/PortalBase.Domain.Tests
dotnet test tests/PortalBase.Application.Tests
PORTALBASE_TEST_MYSQL_ADMIN="Server=127.0.0.1;Port=3306;User Id=root;Password=...;" dotnet test tests/PortalBase.Integration.Tests
PORTALBASE_TEST_MYSQL_ADMIN="..." dotnet test tests/PortalBase.BrowserE2E.Tests
```

Sem `PORTALBASE_TEST_MYSQL_ADMIN`, os testes que precisam de MySQL ficam como pulados (BLOCKED) — nunca simulados.
Rode Integration e BrowserE2E um depois do outro (os dois usam o mesmo MySQL).
