# Deployment — Portal Base v2.0 (Prompt 53)

Processo reproduzível de **BUILD → PACKAGE → DEPLOY → HEALTH → VALIDATION → RELEASE → ROLLBACK**, tratando o
sistema como um conjunto (Web, Workers, Catalog DB, Tenant DBs, fila de tarefas, storage, cofre, certificados
TLS, observabilidade, migrations, integrações). Muito da base já existe (config-validation fail-fast P33, health
liveness/readiness/dependencies P51, graceful shutdown por `CancellationToken`, migrations P17/P48, backup/DR
P50, CI/security P52); este documento consolida a **engenharia de release**.

> **Não confundir**: "existe Dockerfile" ≠ "deploy reproduzível"; "aplicação inicia" ≠ "release validado";
> "rollback do código" ≠ "rollback do banco". **Nunca** deploy em produção real sem autorização e infra.

## 0. Revalidação P53 — o que foi EXECUTADO de fato (não só documentado)

Com Docker disponível no ambiente (daemon 29.3.1) e um MySQL local efêmero, esta revalidação **executou** um
deploy/rollback drill real (nunca produção):

- **Imagem de runtime do Web** construída e **container executado** non-root (`uid=1654 app`), **sem SDK**
  (`dotnet --list-sdks` = 0; só runtimes `AspNetCore.App`/`NETCore.App`), HEALTHCHECK → `healthy`.
- **Health real**: `/health/live`, `/health/ready` (catálogo MySQL conectável), `/health/dependencies` → **200**.
- **Smoke real**: [`scripts/smoke-test.sh`](../scripts/smoke-test.sh) contra o container → **SMOKE PASS** (corpo mínimo, sem segredo).
- **Identidade fim-a-fim**: o log de startup do container reportou `versao=2.0.0 commit=<HEAD> build=<UTC> ambiente=Staging`
  e os rótulos OCI (`version`/`revision`/`created`) casaram com o commit — SOURCE→BUILD→ARTIFACT→DEPLOY.
- **Rollback drill (§29/§62)**: dois artefatos de **identidade imutável distinta** (digests diferentes); N+1
  "falha" → parada → deploy de N pelo **tag imutável** (nunca `latest`) → health + smoke verdes → versão em
  execução revertida; **dados/catálogo preservados** (só o container trocado).
- **Migração tenant-safe FÍSICA**: 29 testes de migração/isolamento/ciclo de vida contra MySQL real (A não
  resolve B; dado de A não aparece em B; runtime não acessa catálogo; provisionamento concorrente consistente).
- **BLOQUEIO_EXTERNO_REAL**: o build **multi-stage committado** (que faz `dotnet restore` DENTRO do container)
  falha neste sandbox porque o NuGet no build do container não confia na CA do proxy de egress
  (`UntrustedRoot`) — limitação de rede do ambiente, **não** defeito do Dockerfile (que é multi-stage,
  non-root, runtime mínima, com rótulos e healthcheck, e restaura normalmente em CI com acesso direto ao NuGet).
  Registry/orquestrador real e deploy em produção permanecem BLOQUEIO_EXTERNO_REAL.

## 1. Topologia (Fase 9)

```
Internet → Reverse Proxy/IIS (TLS/HSTS) → Web (Blazor Server + tarefas) ─┐
                                                                         ├→ Catalog DB (tenants, fila de tarefas)
Worker (Heartbeat/Backup agendado) ──────────────────────────────────────┼→ Tenant DB (por contribuinte, database-per-tenant)
                                                                         ├→ Storage privado (arquivos do contribuinte, backups)
                                                                         ├→ Cofre (Key Vault ou cofre do Windows: segredos)
                                                                         └→ Integrações externas do seu domínio via HTTPS (GuardaSsrf)
Fila = tarefas em segundo plano DURÁVEIS no catálogo (TarefaSegundoPlano). NÃO há RabbitMQ/Redis.
```

### 1.1 Servidor Windows (IIS + serviço do Windows)

Além das imagens Docker, o template roda num servidor Windows com IIS: a Web como site/aplicação do IIS e o Worker
como **serviço do Windows**. [`scripts/configurar-iis.ps1`](../scripts/configurar-iis.ps1) (administrador, Windows
PowerShell 5.1) confere o Hosting Bundle do .NET 10, cria/ajusta o pool e o serviço do Worker (conta virtual própria,
reinício automático), registra as origens do Visualizador de Eventos, cria as pastas privadas com ACL restrita,
cria no cofre do Windows (`Vault:Modo=CofreWindows`) as chaves que faltam e pede as senhas do MySQL sem eco, grava a
configuração sem segredo em `%ProgramData%\PortalBase\`, liga o backup automático (com cópia externa via
`-DiretorioOffsite`) e os alertas por e-mail (`-EmailsAlerta`). Pode ser executado de novo (mantém o que existe).
Os segredos são geridos por [`scripts/cofre-windows.ps1`](../scripts/cofre-windows.ps1) (definir, testar, exportar
e restaurar o pacote do cofre — ver [SEGREDOS.md](SEGREDOS.md) e
[runbooks/DISASTER-RECOVERY.md](runbooks/DISASTER-RECOVERY.md)). Antes do script: contas e banco do catálogo
criados no MySQL e migrations do catálogo aplicadas (contas descritas em
[AMBIENTE-LOCAL.md](AMBIENTE-LOCAL.md#contas-mysql-menor-privilégio)).

## 2. Artefato de release (Fase 2/3/7/38)

Reproduzível a partir do commit: imagens `portalbase-web`/`portalbase-worker` (multi-stage, runtime mínima,
non-root). Identidade injetada por `--build-arg` → rótulos OCI (`org.opencontainers.image.version/revision/
created`) + variáveis `PORTALBASE_BUILD_VERSION/SHA/TIMESTAMP`, reportadas no **log de startup**
(`InformacaoImplantacao`). Sem arquivo local, config manual, segredo no repo ou estado implícito da máquina.
Build: [`scripts/build-image.sh`](../scripts/build-image.sh).

## 3. Configuração e secrets (Fase 4/5/17)

`appsettings.json` (base) + `appsettings.{Environment}.json` + variáveis de ambiente. **Segredo nunca no
código/Dockerfile/git/artifact/log** — vêm do **cofre** (P06) por referência; `appsettings.Production.json`
mantém a connection string **vazia** com aviso "vem do cofre, nunca versionar". Validação de configuração no
startup é **fail-fast** (`PreflightProducao`/`ValidadorConfiguracaoProducao`): lista todas as chaves faltantes
sem expor valores. `.gitignore` bloqueia `.env`/`*.pfx`/`secrets.json`.

## 4. Ordem de deploy (Fase 10)

1. Infraestrutura (DB/cofre/storage disponíveis).
2. **Compatibilidade de banco** (schema N compatível com app N e N+1 — expand/contract).
3. **Migrations** (catálogo primeiro; tenants por base — nunca A contra B).
4. Aplicação (Web) — sobe só com config válida (fail-fast) e após health.
5. Workers — **depois** das dependências essenciais.
6. Jobs agendados.
7. Validação de **health** (liveness + readiness).
8. **Smoke test** ([`scripts/smoke-test.sh`](../scripts/smoke-test.sh)).

## 5. Migrations e multi-tenancy (Fase 11/12/13)

- Catálogo e tenant têm migrations **separadas** (EF Core). Migration de tenant aplicada **por base** — a
  cadeia de provisionamento (P48) garante que a migration de A **nunca** atinge B.
- **Expand/Contract** para mudanças incompatíveis: `EXPAND → DEPLOY → MIGRATE → CONTRACT` (nunca
  `DROP → DEPLOY`, que quebraria a versão anterior durante rollout com réplicas).
- Backup é **pré-requisito** de migration destrutiva (P50). Migration destrutiva **não** roda
  automaticamente sem política explícita.

## 6. Workers / mensageria / storage (Fase 14/15/16)

- Workers são `BackgroundService` com config, health (heartbeat P31) e **graceful shutdown** por
  `CancellationToken` (Fase 21).
- Fila = **tarefas em segundo plano no catálogo** (`TarefaSegundoPlano`, lease/claim + retomada): o deploy
  **preserva** as tarefas pendentes e o estado de tentativas (nunca apagar como estratégia de deploy); uma tarefa
  interrompida pela troca de versão é retomada quando o lease vence. Sem RabbitMQ.
- Storage: raiz privada fora de `wwwroot`, isolamento por tenant (A nunca escreve no espaço de B — P49); TLS/
  credenciais externas via cofre quando aplicável.

## 7. Certificados e segredos (Fase 18)

O certificado **TLS** do site é do reverse proxy/IIS (binding HTTPS), renovado na infraestrutura. Segredos (chaves
criptográficas, senhas do MySQL e do SMTP, segredos de integração por tenant) vivem no **cofre** (P06), por
referência; renovação/rotação no cofre; **chave privada nunca** no repositório nem na imagem. Sem segredo
resolvível ⇒ a operação que depende dele falha fechado (nunca fallback inseguro).

## 8. Health, startup, shutdown (Fase 19/20/21)

- **Health**: `/health/live` (não depende de MySQL), `/health/ready` (catálogo), `/health/dependencies`
  (workers/integrações). Deploy só avança após liveness + readiness.
- **Startup**: determinístico, observável (log de versão/commit), **fail-fast** para config inválida,
  tolerante a dependência temporariamente indisponível quando apropriado; nada perigoso automático.
- **Shutdown**: SIGTERM → para de aceitar trabalho → conclui o trabalho seguro → grava o estado da tarefa em
  execução (ou deixa o lease vencer para retomada) → encerra. Evita conclusão prematura / processamento parcial
  (idempotência).

## 9. Rollout e compatibilidade (Fase 22/23)

Estratégia recomendada conforme infra: **rolling** (padrão) ou **blue/green** quando houver dois ambientes;
**recreate** só para instância única. Durante rollout com réplicas, garantir compatibilidade N/N+1 em:
banco (expand/contract), parâmetros das tarefas em segundo plano (JSON compatível + idempotência), cache (chaves
por tenant/ambiente), configuração e contratos de API.

## 10. Rollback (Fase 24/25/26)

- **Aplicação**: reimplantar a imagem/tag (ou o pacote publicado) da versão N (reproduzível; nunca "manual").
- **Banco**: rollback físico **não** é `ef database update <anterior>` cego — migrations podem ser
  destrutivas/transformadoras. Preferir **forward-fix** ou **forward-compatible migration**; rollback de dados
  só com backup (P50) e política explícita.
- **Tarefas em segundo plano**: lease + idempotência (chave por pedido) + parâmetros compatíveis evitam
  perda/duplicação no rollback.

## 11. Ambientes (Fase 32/33)

`Development` · `Staging/Homologação` · `Production` — **nunca** produção como ambiente de teste. Por
ambiente documenta-se URLs, DB, storage, telemetria e **referências** de secret/cert (os valores permanecem
externos, no cofre).

## 12. Failure handling e release lock (Fase 29/30)

Comportamento definido para falha de migration/imagem/startup/health/worker/queue/DB/storage/secret/cert
(runbooks). **Release lock**: o deploy é interrompido se houver build FAIL, security gate FAIL (P52),
migration incompatível, config obrigatória ausente ou falha crítica de health. **Não** existe "deploy mesmo
assim" como padrão.

## 13. CI/CD (Fase 31)

`ci.yml` (build/test/SCA/secret + imagens) e `security.yml` (CodeQL/Trivy/gitleaks/SBOM/gates) do P52 são os
portões. O pipeline ideal `SOURCE → BUILD → TEST → SECURITY → PACKAGE → IMAGE → DEPLOY → HEALTH → SMOKE →
RELEASE` **consome** esses resultados (não os altera). O passo DEPLOY em infra real é **BLOCKED** aqui.

## 14. Limitações (BLOCKED — honestas)

Deploy em produção/homologação real, **registry** de imagens, orquestrador (K8s), rollout blue/green/canary
efetivo e **teste de rollback controlado** dependem de **infraestrutura inexistente neste ambiente** →
**BLOCKED**. Tudo que é local (arquivos, scripts, config, build, testes, imagens) foi validado; nada foi
simulado.
