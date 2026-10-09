# perf/ — Carga & Performance

Artefatos de avaliação de capacidade do portal. **Nenhum número inventado**: o que exige MySQL/Web/Worker/infra de
carga só é medido num ambiente dedicado.

## Ferramentas

- **k6** (`load-test.js` + `totp.js` + `datasets.js`) — carga **HTTP** (smoke/baseline/load/stress/soak/spike).
- **Playwright** (`tests/PortalBase.BrowserE2E.Tests/PerformanceSmokeE2ETests.cs`) — latência de render pelo navegador
  (poucos usuários).
- **Testes de integração** (`TransacaoRollbackTests`, isolamento e paginação contra MySQL real) — corretude sob
  concorrência.
- **Métricas do app** (OpenTelemetry `Meter`: `MetricasOperacionais`, `MetricasProvisionamento`) — throughput, erros,
  duração e saúde operacional.

## Regra de segurança

NUNCA rode carga contra produção, API externa real ou banco de cliente — apenas um ambiente **dedicado/descartável**.
**Nada** é hardcoded: `BASE_URL`, credenciais, tenant e segredo de MFA vêm do ambiente. As credenciais são de um
**usuário de teste semeado** no ambiente de carga. Sem credenciais, o harness exercita só endpoints **anônimos**
(liveness + render de login) — sem bypass de autenticação.

## Configuração (variáveis de ambiente)

| Variável | Uso |
|----------|-----|
| `BASE_URL` | Alvo dedicado (ex.: `http://127.0.0.1:8080`). |
| `PERFIL` | `smoke` \| `baseline` \| `load` \| `stress` \| `soak` \| `spike`. |
| `VUS` / `DURATION` / `THINK_TIME` | Ajustes finos do perfil escolhido. |
| `LOAD_TEST_USER` / `LOAD_TEST_PASSWORD` / `LOAD_TEST_TENANT` / `LOAD_TEST_MFA_SECRET` | Usuário de teste semeado (um tenant). |
| `LOAD_TEST_USERS_JSON` | Vários usuários de teste (um por tenant), em JSON. |

## Uso

```bash
BASE_URL=http://127.0.0.1:8080 k6 run perf/load-test.js
BASE_URL=http://127.0.0.1:8080 PERFIL=stress k6 run perf/load-test.js
```

`datasets.js` define perfis **SMALL/MEDIUM/LARGE** (tenants, usuários, arquivos, registros). São parâmetros de teste
sintético, **não** capacidade declarada do produto. Acrescente ao `load-test.js` as telas e consultas do domínio do
seu portal.
