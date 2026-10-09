# Observabilidade, Monitoramento e Operações — Portal Base (Prompt 31)

Esta camada torna o Portal Base **operável em produção**: responder rapidamente "está funcionando?", "qual
componente/tenant foi afetado?", "onde o fluxo parou?", "está degradando?". Preserva **todos** os controles de
segurança/LGPD do P30: **nunca** expõe segredo, token, chave privada, conteúdo de arquivo ou PII em logs, métricas,
traces, tags ou alertas (§3/§80).

## 1. Princípio arquitetural (§2) — cada mecanismo no seu papel

| Mecanismo | Papel | Implementação |
|---|---|---|
| **LOG** | diagnóstico técnico | Serilog estruturado (Console + OTLP), correlação, sem payload |
| **METRIC** | tendência quantitativa | OpenTelemetry Meters (contadores/gauges), cardinalidade baixa |
| **TRACE** | correlação ponta a ponta | OpenTelemetry (AspNetCore), CorrelationId/TraceId |
| **AUDIT** | accountability da operação | trilha append-only + HMAC (P05) |
| **ALERT** | condição que exige ação | rotina de alertas (§10) + ponte alerta→incidente |
| **HEALTH** | estado técnico do componente | health checks liveness/readiness/dependency |

Nunca usar um como substituto do outro (ex.: log de console **não** é auditoria — §25).

## 2. Correlação ponta a ponta (§4)

O middleware Web injeta `X-Correlation-ID` (gera se ausente) + `TraceId` no log estruturado E no contexto de
auditoria em todo request. Os workers e o executor de tarefas em segundo plano definem correlação por
ciclo/tarefa/tenant. Assim rastreia-se `HTTP → Application → DB → tarefa em segundo plano → Worker` por
`CorrelationId`/`TraceId` — **sem** conteúdo de negócio no trace (§12).

## 3. Structured logging (§5/§6/§7)

Campos técnicos: Timestamp, Level, Application, Environment, CorrelationId, TraceId, Component, Result, ErrorCode
(tipo da exceção), DurationMs quando aplicável. **Nunca** PII/payload. Produção não depende de `Debug` para
operação normal; polling usa `Debug` (não inunda `Information`). Sampling recomendado para health/polling
repetitivos — **nunca** para incidentes, falhas críticas, segurança, auditoria ou operações críticas de negócio (§7).

## 4. Métricas (§8) — catálogo

Meters emitidos (todos OpenTelemetry, exportáveis por OTLP):

| Meter | Origem | Exemplos |
|---|---|---|
| `PortalBase.Provisionamento` | P09 | provisioning/failures |
| `PortalBase.Operacional` | **P31** | worker_active/worker_stalled/worker_seconds_since_success/worker_processed_total/worker_errors_total/worker_restarts_total/integration_seconds_since_success |

Ao acrescentar módulos do seu domínio (filas, integrações), crie meters próprios seguindo as mesmas regras de
cardinalidade (abaixo) e registre-os na composição da Web e do Worker.

### Cardinalidade e dados sensíveis (§9/§10)
**Não** se usa `tenantId`, CNPJ, CPF, protocolo, conta ou valor como **label** (explosão de cardinalidade /
PII). As únicas dimensões do meter operacional são `worker` e `integration` (conjunto pequeno e fixo). O
diagnóstico de um tenant específico se faz por **logs/traces/auditoria/consultas operacionais autorizadas**, não
por métricas rotuladas por tenant. Enforçado por teste (`MetricasOperacionaisTests`).

## 5. Distributed tracing (§11/§12)

OpenTelemetry com instrumentação AspNetCore (Web) e recurso nomeado por serviço (Web/Worker). Exporter OTLP
**apenas** quando `OTEL_EXPORTER_OTLP_ENDPOINT` estiver definido (sem endpoint, não exporta — nada é forçado).
Nunca PII em baggage/tags. Um fluxo é rastreável de ponta a ponta **sem** armazenar conteúdo de negócio no trace.

## 6. Health checks (§13-19)

Separação real, exposta pelo host Web:

| Endpoint | Tag | Verifica | Regra |
|---|---|---|---|
| `/health/live` | live | `VerificacaoVivacidade` — só "processo vivo" | **NÃO** verifica dependências (§14): dependência caída não causa restart infinito |
| `/health/ready` | ready | `VerificacaoBancoCatalogo` — banco crítico acessível (`CanConnect`, barato) | readiness da função (§15/§19) |
| `/health/dependencies` | dependencies | `VerificacaoSaudeWorkers` (estagnação), `VerificacaoServicoWorker` (batida do Worker gravada no catálogo) + `VerificacaoUltimaIntegracao` (frescor) | **Degraded**/**Unhealthy** sem derrubar readiness (§20/§16) |

- **Integrações externas (§16/§44):** a saúde deriva do **frescor do último sucesso** registrado pelos clientes
  (`IRegistroUltimaIntegracao`) — **nunca** dispara chamada externa por probe. O template não traz integração
  externa; o contrato fica pronto para as do seu domínio.
- **Key Vault (§17):** não se lê o segredo no probe; a falha de resolução é observada pelos fluxos que a usam.
- **Storage/Banco (§18/§19):** verificações baratas; nunca upload grande nem consulta cara por probe.

## 7. Operação dos workers (§20/§21)

`IRegistroSaudeWorkers` (singleton em memória) recebe de cada worker: início, **batida (heartbeat)**, sucesso
(+itens), erro (**só o código/tipo**), encerramento. Deriva:

- **Heartbeat / última batida / último sucesso / último erro / contadores / reinícios** por worker.
- **Detecção de estagnação (§21):** worker ativo sem batida além de `Observabilidade:LimiteEstagnacaoSegundos`
  (configurável, não arbitrário) ⇒ `worker_stalled=1` + health `Degraded`. Processo vivo mas parado é detectado.

Workers instrumentados: `heartbeat` (`TrabalhadorHeartbeat` — sinal de liveness do host, que também grava no
catálogo o snapshot da saúde de todos os trabalhadores para a Web exibir em **Operações → Workers**) e
`backup-agendado` (`TrabalhadorBackup`). A saúde é exposta por **métricas** (padrão para processos de background),
pelos health checks e pela tela de Operações. O host do Worker publica os Meters via OTLP.

## 8. Monitoramento de filas e fluxo (§22-28)

O template traz uma **fila durável de tarefas em segundo plano** (`TarefaSegundoPlano`, no catálogo), executada na
Web por `HospedeiroTarefas`/`ExecutorTarefasSegundoPlano`: a tela só enfileira; o executor reivindica a tarefa por
um prazo (lease renovado enquanto roda), grava o resultado e retoma tarefas interrompidas até
`TarefaSegundoPlano.MaximoTentativas`. O manipulador de exemplo (`ManipuladorTarefaExemplo`) mostra o contrato a
copiar para cada trabalho pesado do seu domínio. Sinais de backlog das filas do seu domínio devem vir de
**consultas operacionais periódicas** (não por probe — §19), com os alertas correspondentes na rotina do §10.

## 9. Integrações externas (§29/§30/§31/§44)

Integrações que o seu domínio introduzir devem expor métricas técnicas comuns (requests/success/failure/latency)
sem rótulos de alta cardinalidade nem conteúdo, registrar o último sucesso/falha em `IRegistroUltimaIntegracao`
(exibido em **Operações → Integrações**) e ter timeout, retry controlado e `CancellationToken` (regra 11). Nunca se
loga credencial/payload; segredos por tenant ficam no cofre (ver [SEGREDOS.md](SEGREDOS.md)).

## 10. Alertas (§39-41) e integração com incidentes (§50)

Severidades: INFO / WARNING / HIGH (Alto) / CRITICAL (Critico). A **ponte alerta→incidente**
(`ServicoPonteAlertaIncidente`) audita **todo** alerta e, acima de `Observabilidade:SeveridadeMinimaIncidente`,
abre um **incidente TÉCNICO** (P30) — **nunca** `ComDadosPessoais` automaticamente (§50): a classificação de
risco ao titular permanece HUMANA. Alertas recorrentes **deduplicam** (uma nota no incidente aberto — §41),
evitando fadiga.

**Chamador real (OBS-004):** a rotina `MonitorAlertas` da Web (a cada `Observabilidade:IntervaloAlertasMinutos`,
padrão 5 min) executa `CicloAlertasOperacionais`, que avalia:

| Alerta | Severidade | Condição |
|---|---|---|
| `WORKER-FORA-DO-AR` | Crítico | o serviço do Worker nunca registrou, parou ou está sem batida |
| `WK-FALHA-<worker>` / `WK-TRAVADO-<worker>` | Alto | trabalhador do Worker em falha ou estagnado |
| `BACKUP-ATRASADO` | Crítico | backup do catálogo mais antigo que `Observabilidade:AlertaBackupHoras` |
| `DISCO-<unidade>` | Alto / Crítico | livre abaixo de `DiscoLivreMinimoPercentual`/`DiscoLivreMinimoGb` (Crítico abaixo da metade) |
| `EMAIL-NAO-ENVIADO` | Alto | e-mails da fila de disparo não enviados |

Cada alerta é entregue por três caminhos independentes: log (Visualizador de Eventos do Windows e arquivo de log),
esta ponte (auditoria/incidente) e e-mail para `Observabilidade:EmailsAlerta`. Uma fonte que falha é registrada e
pulada — as outras seguem.

## 11. Auditoria operacional (§51/§52)

Ações administrativas críticas geram auditoria dedicada (sem payload): `AlertaOperacionalDisparado`,
`IntegracaoHabilitada`/`Desabilitada`, `ConfiguracaoOperacionalAlterada`, `WorkerDesbloqueado`,
`ThresholdOperacionalAlterado`, `ManutencaoOperacionalExecutada`. Health probes **não** são auditados individualmente.

## 12. Capacidade, SLI/SLO, backpressure (§35-38/§45/§46)

- **Capacidade (§35):** CPU/memória/threadpool/GC via instrumentação de runtime (OTel runtime instrumentation,
  a habilitar no deploy) + os gauges operacionais.
- **SLI/SLO (§36/§37/§38):** devem ser definidos por quem adota o template como **alvos técnicos configuráveis**
  (ver o orçamento de performance da Lei 03, [LEI-03-AUDITORIA-CONTINUA.md](LEI-03-AUDITORIA-CONTINUA.md)) — não
  representam SLA contratual.
- **Backpressure (§45):** fila durável de tarefas com lease/claim e limite de execuções simultâneas
  (`Tarefas:ExecucoesSimultaneas`), rate limiting no Web e polling com teto — consumo nunca ilimitado.

## 13. Dashboards (§47/§62) e runbooks (§49)

A implementação concreta de dashboards depende da plataforma de observabilidade (Grafana/Prometheus, Seq,
Application Insights) — **não configurada neste repositório**. Dentro do portal, a área **Operações** (Saúde,
Workers, Integrações, Backups) mostra o estado atual. Os runbooks ficam em `docs/runbooks/` (ex.:
[DISASTER-RECOVERY.md](runbooks/DISASTER-RECOVERY.md)); acrescente os do seu domínio.

## 14. Configuração (§53/§54)

Limiares operacionais em `Observabilidade:*` (appsettings): `LimiteEstagnacaoSegundos`,
`HeartbeatIntervaloSegundos`, `LimiteIntegracaoStaleSegundos`, `WorkerEsperado`, `SeveridadeMinimaIncidente`,
`AlertasHabilitados`, `EmailsAlerta`, `IntervaloAlertasMinutos`, `RenotificarAlertaHoras`, `AlertaBackupHoras`,
`DiscoLivreMinimoPercentual`, `DiscoLivreMinimoGb`. **Sem segredo.** O versionamento da configuração efetiva
(nome/versão/hash da config não secreta/timestamp/ambiente — §54) e a detecção de drift (§53) são recomendados no
deploy; nunca registram valores secretos.

## 15. Limitações declaradas (§81)

- Plataforma de observabilidade externa (Prometheus/Grafana/Seq/App Insights/OTLP collector) **NÃO configurada**
  aqui — o sistema **emite** métricas/traces/health padrão OTel/ASP.NET; a coleta/visualização/alerting é deploy.
- Health/heartbeat de workers em memória é **per-processo** (host do Worker) — exposto por métricas; a Web enxerga
  o Worker pela batida gravada no catálogo (`VerificacaoServicoWorker`). O host do Worker não expõe endpoint HTTP
  de health (não adicionado para não acoplar um servidor web ao Worker).
- Gauges de backlog por fila (oldest-age, pending-count) não são emitidos pelo template; acrescente-os junto das
  filas do seu domínio.
- Instrumentação de runtime (CPU/GC/threadpool) e circuit breaker (§59) são recomendados no deploy; a
  arquitetura já provê retry/backoff/lease/idempotência.
