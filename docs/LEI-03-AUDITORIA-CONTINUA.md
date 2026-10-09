# LEI 03 — AUDITORIA CONTÍNUA

**Governança Técnica, Monitoramento, Prevenção de Regressão e Saúde Permanente do Sistema**

| | |
|---|---|
| Versão | 1.0 |
| Classificação | Norma Técnica Obrigatória |
| Aplicabilidade | Todo sistema que esteja em desenvolvimento, homologação ou produção. |

> Norma fornecida pelo responsável pelo Portal Base em 08/10/2026 e adotada como obrigatória neste repositório, junto
> com a [Lei Técnica Universal](LEI-TECNICA-UNIVERSAL.md) (no ciclo abaixo, a "Lei 01 — Desenvolver corretamente"), o
> [Prompt-Mestre de auditoria](LEI-02-AUDITORIA-RECUPERACAO-MODERNIZACAO.md) (a "Lei 02 — Recuperar e modernizar") e as
> [Regras Transversais Obrigatórias](REGRAS-TRANSVERSAIS-OBRIGATORIAS.md). O texto abaixo é o original; nada foi
> acrescentado às regras.

---

## 1. Missão

Esta norma tem como objetivo impedir que um sistema:

- volte a apresentar problemas já corrigidos;
- acumule nova dívida técnica sem controle;
- sofra degradação progressiva de performance;
- apresente crescimento descontrolado do banco;
- perca qualidade arquitetural;
- acumule código morto;
- desenvolva novos gargalos;
- perca cobertura de testes;
- tenha falhas de backup;
- perca observabilidade;
- sofra regressões de segurança;
- ultrapasse sua capacidade operacional sem identificação prévia.

O sistema deverá ser tratado como um produto em evolução contínua.

> **Sistema saudável não é aquele que foi corrigido uma vez. É aquele que continua saudável enquanto evolui.**

---

## 2. Princípio da auditoria contínua

Nenhuma auditoria poderá ser considerada definitiva.

Toda alteração significativa poderá introduzir novos problemas.

Portanto:

```text
DESENVOLVIMENTO
↓
TESTE
↓
DEPLOY
↓
MONITORAMENTO
↓
AUDITORIA
↓
MELHORIA
↓
NOVO DESENVOLVIMENTO
```

Esse ciclo deverá permanecer ativo durante toda a vida do sistema.

---

## 3. Regra de não regressão

Nenhuma nova implementação poderá degradar uma métrica ou comportamento existente sem justificativa técnica formal.

Deverão ser monitorados, quando aplicável:

- tempo de resposta;
- P50;
- P95;
- P99;
- taxa de erro;
- throughput;
- CPU;
- memória;
- conexões;
- queries;
- filas;
- cache;
- tamanho das tabelas;
- tempo de jobs;
- cobertura de testes.

---

## 4. Baseline obrigatória

Depois que um sistema for estabilizado, deverá ser criado um **Baseline Técnico**.

Exemplo:

```text
API P95              = 420 ms
API P99              = 1,2 s
Erro                 = 0,3%
CPU média            = 48%
RAM média            = 62%
DB Connections       = 35%
Cache Hit Rate       = 87%
Queue Backlog        = 120
```

Esse baseline será utilizado para detectar degradação futura.

---

## 5. Regra de comparação

Toda alteração relevante deverá ser comparada com o baseline.

Exemplo:

```text
ANTES
P95 = 420 ms

DEPOIS
P95 = 680 ms
```

Mesmo que a funcionalidade esteja funcionando, existe uma regressão de performance que deverá ser investigada.

---

## 6. Orçamento de performance

O sistema deverá possuir limites máximos aceitáveis.

Exemplo:

```text
P95 API        <= 500 ms
P99 API        <= 2 s
Erro           <= 1%
CPU            <= 70%
RAM            <= 80%
DB Connections <= 70%
```

Os valores deverão ser definidos conforme o sistema.

Quando um limite for ultrapassado:

```text
Métrica excedeu limite
↓
Alerta
↓
Investigação
↓
Causa raiz
↓
Correção
```

---

## 7. Regra de performance budget

Cada componente crítico deverá possuir um orçamento de recursos.

Exemplos:

```text
Página
→ máximo de X MB

API
→ máximo de X ms

Endpoint
→ máximo de X queries

Payload
→ máximo de X MB

Consulta
→ máximo de X segundos
```

Novas funcionalidades não poderão consumir recursos indefinidamente.

---

## 8. Regra de consultas

Consultas críticas deverão ser monitoradas continuamente.

Identificar:

- aumento de tempo;
- aumento de volume;
- mudança de plano;
- perda de índice;
- aumento de frequência;
- aumento de concorrência.

Uma query que era rápida com 10 mil registros poderá se tornar lenta com 10 milhões.

---

## 9. Regra de N+1 contínuo

N+1 deverá ser tratado como uma categoria permanente de auditoria.

Novas funcionalidades deverão ser verificadas antes do deploy.

Nenhum novo endpoint crítico deverá entrar em produção sem análise de suas consultas.

---

## 10. Regra de paginação contínua

Toda nova listagem deverá obedecer às regras de paginação.

Verificar:

```text
Paginação
Limite máximo
Ordenação
Filtros
Cursor/Keyset quando necessário
```

É proibido criar novas telas que carreguem grandes volumes de dados sem justificativa.

---

## 11. Regra de limite de payload

Toda API deverá possuir limites adequados.

Avaliar:

- request;
- response;
- upload;
- download;
- exportação.

Payloads excessivos deverão ser tratados como risco de performance e disponibilidade.

---

## 12. Regra de banco em crescimento

O banco deverá ser monitorado continuamente.

Acompanhar:

```text
Database Size
Table Size
Index Size
Growth Rate
Storage
Connections
Locks
Slow Queries
```

Deverá ser possível estimar:

> Quando o banco atingirá determinado tamanho?

---

## 13. Regra de crescimento de tabelas

Tabelas críticas deverão possuir acompanhamento de crescimento.

Exemplo:

```text
Logs
Eventos
Auditoria
Pedidos
Documentos
Integrações
Mensagens
```

Nenhuma tabela operacional deverá crescer indefinidamente sem estratégia.

---

## 14. Regra de retenção contínua

Periodicamente verificar se as políticas de retenção estão funcionando.

Exemplo:

```text
Logs > 90 dias
→ arquivar/remover

Eventos > 180 dias
→ arquivar

Dados fiscais
→ manter conforme requisito legal
```

---

## 15. Regra de jobs

Todo job deverá ser monitorado.

Registrar:

```text
Última execução
Duração
Sucesso
Falha
Quantidade processada
Retry
Backlog
```

Detectar:

```text
Job demorando cada vez mais
```

como possível sinal de degradação.

---

## 16. Regra de filas

Filas deverão ser monitoradas continuamente.

Indicadores:

```text
Queue Depth
Consumer Rate
Producer Rate
Retry Count
DLQ Count
Processing Time
```

Se:

```text
Produção > Consumo
```

por período significativo, existe risco de acúmulo.

---

## 17. Regra de dead letter queue

Mensagens na DLQ deverão ser acompanhadas.

Não permitir:

```text
DLQ = 1.000.000 mensagens
```

sem investigação.

Toda mensagem que entrar na DLQ deverá possuir mecanismo de:

- investigação;
- correção;
- reprocessamento;
- descarte controlado.

---

## 18. Regra de cache

Monitorar:

```text
Hit Rate
Miss Rate
Memory
Evictions
Expiration
Invalidation
```

Cache degradando poderá indicar:

- chave inadequada;
- TTL inadequado;
- estratégia incorreta;
- crescimento excessivo.

---

## 19. Regra de memória

Monitorar continuamente:

```text
Memory Usage
GC
Heap
Allocation
LOH
Memory Growth
```

Investigar crescimento progressivo.

Exemplo:

```text
10h → 50%
12h → 60%
14h → 70%
16h → 80%
```

pode indicar vazamento.

---

## 20. Regra de CPU

CPU elevada deverá ser investigada.

Não aumentar simplesmente o número de CPUs.

Primeiro verificar:

- código;
- queries;
- serialização;
- loops;
- concorrência;
- jobs;
- processos externos.

---

## 21. Regra de escalabilidade

Periodicamente avaliar:

```text
Usuários
Tenants
Dados
Requests
Jobs
Integrações
```

comparando com a capacidade atual.

---

## 22. Regra de capacidade

Deverá existir uma estimativa de capacidade.

Exemplo:

```text
Sistema suporta:

1.000 req/s
50.000 usuários
100 tenants
500 milhões de registros
```

Quando a utilização atingir determinado percentual:

```text
70% → atenção
80% → planejamento
90% → ação imediata
```

Os limites deverão ser definidos de acordo com o ambiente.

---

## 23. Regra de capacity planning

Sempre que houver crescimento significativo deverá ser realizada análise de capacidade.

Perguntas obrigatórias:

```text
O banco suporta?

A API suporta?

A infraestrutura suporta?

A fila suporta?

O storage suporta?

O backup suporta?

O monitoramento suporta?
```

---

## 24. Regra de backup contínuo

Monitorar:

- execução;
- duração;
- tamanho;
- sucesso;
- falha;
- retenção;
- armazenamento.

Backup que falhou deverá gerar alerta.

---

## 25. Regra de restore test

Periodicamente deverá ser realizado teste de restauração.

Validar:

```text
Backup
↓
Restore
↓
Integridade
↓
Aplicação
↓
Dados
```

O objetivo é comprovar que o backup continua utilizável.

---

## 26. Regra de RPO/RTO

Periodicamente validar se o ambiente ainda atende:

```text
RPO
RTO
```

O crescimento do sistema pode tornar uma estratégia antiga insuficiente.

---

## 27. Regra de segurança contínua

Segurança deverá ser reavaliada periodicamente.

Verificar:

- dependências vulneráveis;
- certificados;
- secrets;
- permissões;
- endpoints;
- autenticação;
- autorização;
- logs;
- exposição de dados.

---

## 28. Regra de dependências

Dependências deverão ser monitoradas.

Identificar:

```text
Obsoleta
Vulnerável
Sem manutenção
Incompatível
Desnecessária
```

Atualizações deverão ser planejadas.

---

## 29. Regra de código morto

Periodicamente identificar:

- métodos sem uso;
- classes sem uso;
- endpoints sem consumidores;
- tabelas sem uso;
- configurações antigas;
- features abandonadas.

A remoção deverá ocorrer somente após análise de impacto.

---

## 30. Regra de dívida técnica

Toda dívida técnica identificada deverá ser registrada.

Exemplo:

| ID | Dívida | Impacto | Prioridade | Plano |
|---|---|---|---|---|
| DT-001 | Serviço monolítico | Alto | P1 | Refatorar |
| DT-002 | Query sem índice | Alto | P1 | Corrigir |
| DT-003 | Código duplicado | Médio | P2 | Refatorar |

---

## 31. Regra de arquitetura

A arquitetura deverá ser revisada periodicamente.

Verificar:

- acoplamento;
- dependências;
- limites de contexto;
- serviços;
- banco;
- filas;
- cache;
- integrações.

Uma arquitetura que funcionava com 10 usuários pode não ser adequada com 100.000.

---

## 32. Regra de ADR

Decisões arquiteturais importantes deverão ser registradas.

Exemplo:

```text
ADR-001
Por que Redis?

ADR-002
Por que RabbitMQ?

ADR-003
Por que banco por tenant?

ADR-004
Por que Keyset Pagination?
```

---

## 33. Regra de observabilidade

Não permitir que o sistema entre em produção sem capacidade adequada de diagnóstico.

Deverá ser possível identificar:

```text
O que aconteceu?
Quando?
Onde?
Com quem?
Por quê?
Quanto tempo demorou?
Qual componente falhou?
```

---

## 34. Regra de alertas

Alertas deverão ser baseados em impacto.

Evitar excesso de alertas irrelevantes.

Priorizar:

```text
Critical
Warning
Informational
```

---

## 35. Regra de incidentes

Todo incidente relevante deverá possuir análise posterior.

Processo:

```text
Incidente
↓
Mitigação
↓
Recuperação
↓
Root Cause Analysis
↓
Correção
↓
Prevenção
```

---

## 36. Regra de post-mortem

Incidentes críticos deverão gerar documentação contendo:

- o que aconteceu;
- impacto;
- duração;
- causa;
- detecção;
- resposta;
- correção;
- prevenção.

O objetivo não é encontrar culpados.

O objetivo é impedir repetição.

---

## 37. Regra de regressão automatizada

Problemas importantes descobertos deverão, quando possível, gerar um teste automatizado.

Exemplo:

```text
Bug encontrado
↓
Correção
↓
Teste
↓
Teste permanente
```

Assim o problema não deverá retornar silenciosamente.

---

## 38. Regra de testes

A suíte de testes deverá evoluir junto com o sistema.

Monitorar:

```text
Quantidade
Cobertura
Tempo
Falhas
Flakiness
```

Testes instáveis deverão ser tratados como problema.

---

## 39. Regra de testes de performance

Testes de performance deverão ser executados periodicamente.

Comparar:

```text
Baseline
vs
Versão atual
```

Detectar regressões.

---

## 40. Regra de CI/CD

O pipeline deverá impedir, quando possível, que problemas críticos avancem.

Exemplo:

```text
Build
↓
Unit Tests
↓
Integration Tests
↓
Security Scan
↓
Quality Gate
↓
Performance Gate
↓
Deploy
```

---

## 41. Regra de quality gate

Definir critérios mínimos.

Exemplo:

```text
Build       = OK
Tests       = 100% críticos OK
Security    = Sem vulnerabilidade crítica
Performance = Dentro do orçamento
Coverage    = Dentro da meta
```

---

## 42. Regra de deploy progressivo

Para alterações de alto risco avaliar:

```text
Canary
Blue/Green
Feature Flag
Gradual Rollout
```

Evitar disponibilizar imediatamente uma mudança de alto risco para 100% dos usuários.

---

## 43. Regra de feature flag

Funcionalidades críticas poderão utilizar feature flags para:

- ativação gradual;
- rollback rápido;
- teste controlado;
- segmentação.

Feature flags antigas deverão ser removidas.

---

## 44. Regra de produção

Produção deverá ser considerada ambiente crítico.

Não realizar testes destrutivos diretamente em produção.

Alterações emergenciais deverão ser documentadas posteriormente.

---

## 45. Regra de auditoria periódica

O sistema deverá passar por auditorias periódicas.

A frequência dependerá da criticidade.

Exemplo:

```text
Crítico:
mensal

Alto:
trimestral

Médio:
semestral

Baixo:
anual
```

---

## 46. Checklist mínimo da auditoria

Toda auditoria deverá verificar:

```text
[ ] Performance
[ ] Banco
[ ] Queries
[ ] N+1
[ ] Paginação
[ ] APIs
[ ] Frontend
[ ] Cache
[ ] Filas
[ ] Workers
[ ] Memória
[ ] CPU
[ ] Storage
[ ] Segurança
[ ] Multi-tenancy
[ ] Logs
[ ] Métricas
[ ] Tracing
[ ] Backup
[ ] Restore
[ ] Disaster Recovery
[ ] Dependências
[ ] Código morto
[ ] Dívida técnica
[ ] Testes
[ ] CI/CD
[ ] Capacidade
```

---

## 47. Relatório de saúde do sistema

A auditoria deverá produzir um relatório contendo:

1. **Saúde geral** — Excelente, Bom, Atenção ou Crítico.
2. **Performance** — estado atual e evolução.
3. **Banco** — crescimento e gargalos.
4. **Infraestrutura** — consumo e capacidade.
5. **Segurança** — riscos atuais.
6. **Backup** — estado do backup e restore.
7. **Observabilidade** — qualidade dos dados disponíveis.
8. **Dívida técnica** — problemas acumulados.
9. **Capacidade** — quanto o sistema suporta atualmente.
10. **Riscos futuros** — o que provavelmente será problema nos próximos meses.

---

## 48. Score de saúde

Quando possível, atribuir uma pontuação:

```text
Performance       0–100
Arquitetura       0–100
Segurança         0–100
Banco             0–100
Escalabilidade    0–100
Observabilidade   0–100
Backup/DR         0–100
Testes            0–100
Manutenção        0–100
```

E gerar:

```text
SYSTEM HEALTH SCORE
```

O score não substitui análise técnica; serve para acompanhamento da evolução.

---

## 49. Regra de tendência

Não analisar somente o valor atual.

Analisar evolução.

Exemplo:

```text
CPU

Janeiro   45%
Fevereiro 51%
Março     58%
Abril     67%
```

Mesmo estando abaixo do limite, existe tendência de crescimento.

Isso deverá gerar planejamento preventivo.

---

## 50. Regra de previsão

Quando houver dados suficientes, estimar:

```text
Quando o storage ficará cheio?

Quando o banco atingirá X TB?

Quando a CPU ultrapassará 80%?

Quando a fila poderá acumular?

Quando o backup ficará inviável?
```

O objetivo é agir **antes da falha**.

---

## 51. Regra de prevenção

Sempre que um problema for corrigido, perguntar:

> Como impedir que esse problema volte?

Exemplo:

```text
Problema:
N+1

Correção:
Projeção

Prevenção:
Regra de análise de queries
+
Teste
+
Code Review
+
Monitoramento
```

---

## 52. Regra de causa raiz

Problemas recorrentes deverão gerar análise arquitetural.

Se o mesmo problema aparece várias vezes:

```text
Não corrigir apenas o caso.

Corrigir o mecanismo que permite que o problema aconteça.
```

---

## 53. Regra de governança

Nenhuma equipe deverá adicionar continuamente funcionalidades sem reservar capacidade para:

- manutenção;
- segurança;
- performance;
- atualização;
- testes;
- dívida técnica;
- infraestrutura.

O sistema deverá possuir espaço no roadmap para sua própria evolução técnica.

---

## 54. Regra de evolução

A arquitetura deverá acompanhar o crescimento do negócio.

Mudanças de:

```text
Usuários
Dados
Processos
Integrações
Requisitos
```

deverão provocar nova análise arquitetural quando necessário.

---

## 55. Regra de alerta antecipado

Sempre que uma métrica estiver se aproximando de um limite, deverá existir planejamento preventivo.

Exemplo:

```text
60% → observar
70% → planejar
80% → executar
90% → crítico
```

Os valores deverão ser adaptados ao sistema.

---

## 56. Regra de documentação viva

A documentação deverá refletir o estado atual do sistema.

Não permitir:

```text
Arquitetura documentada
≠
Arquitetura real
```

Sempre que uma alteração arquitetural ocorrer:

```text
Código
+
Infraestrutura
+
Documentação
```

deverão permanecer alinhados.

---

## 57. Regra de auditoria final

Após cada ciclo de melhorias:

```text
Auditoria
↓
Correções
↓
Testes
↓
Métricas
↓
Nova auditoria
```

O ciclo somente será encerrado quando:

- problemas críticos forem tratados;
- regressões forem eliminadas;
- métricas estiverem dentro dos limites;
- riscos restantes estiverem documentados.

---

## 58. Definition of Done

Uma melhoria somente poderá ser considerada concluída quando:

```text
✓ Problema identificado
✓ Causa raiz conhecida
✓ Correção implementada
✓ Testes realizados
✓ Regressão validada
✓ Performance validada
✓ Segurança validada
✓ Métricas comparadas
✓ Monitoramento configurado
✓ Alertas configurados quando necessários
✓ Documentação atualizada
✓ Risco residual registrado
```

---

## 59. Instrução final para a IA

Ao receber uma nova funcionalidade, correção ou alteração em um sistema que esteja sob esta norma:

**Antes de implementar, obrigatoriamente verificar:**

1. O código existente.
2. A arquitetura.
3. As dependências.
4. O banco.
5. As consultas.
6. A performance.
7. A segurança.
8. A observabilidade.
9. A escalabilidade.
10. Os testes existentes.

**Depois da implementação:**

1. Executar testes.
2. Comparar métricas.
3. Verificar regressões.
4. Verificar banco.
5. Verificar logs.
6. Verificar performance.
7. Verificar segurança.
8. Atualizar documentação.
9. Registrar riscos.
10. Confirmar critérios de aceite.

**Nunca assumir que uma alteração pequena possui impacto pequeno.**

Uma alteração de uma linha pode alterar:

- plano de execução;
- consumo de memória;
- concorrência;
- segurança;
- comportamento de uma API;
- performance de milhões de registros.

---

## 60. Princípio supremo

> **TODO SISTEMA DEVE SER MONITORADO, MEDIDO, AUDITADO E EVOLUÍDO.**
>
> **TODA REGRESSÃO DEVE SER DETECTADA.**
>
> **TODO PROBLEMA RECORRENTE DEVE GERAR UMA CORREÇÃO PREVENTIVA.**
>
> **TODA CORREÇÃO IMPORTANTE DEVE GERAR EVIDÊNCIA DE QUE FUNCIONOU.**
>
> **O SISTEMA NÃO DEVE ESPERAR A FALHA PARA REAGIR.**

---

## Ciclo oficial das três leis

```text
┌──────────────────────────────────────┐
│ LEI 01                               │
│ DESENVOLVER CORRETAMENTE             │
│                                      │
│ Projeto → Arquitetura → Desenvolvimento│
│ → Testes → Produção                 │
└──────────────────┬───────────────────┘
                   ↓
        ┌──────────────────────┐
        │ SISTEMA EM PRODUÇÃO  │
        └──────────┬───────────┘
                   ↓
┌──────────────────────────────────────┐
│ LEI 02                               │
│ RECUPERAR E MODERNIZAR               │
│                                      │
│ Auditar → Diagnosticar → Corrigir   │
│ → Testar → Validar                   │
└──────────────────┬───────────────────┘
                   ↓
┌──────────────────────────────────────┐
│ LEI 03                               │
│ MANTER SAUDÁVEL                      │
│                                      │
│ Monitorar → Medir → Auditar →       │
│ Prevenir → Evoluir                   │
└──────────────────┬───────────────────┘
                   │
                   └───────────────┐
                                   ↓
                         NOVA EVOLUÇÃO
                                   │
                                   └──→ LEI 01
```

## Regra máxima

**O ciclo nunca termina.**

Um sistema não é considerado tecnicamente saudável porque foi desenvolvido corretamente uma vez.

Ele é considerado saudável quando existe um processo contínuo capaz de:

**prevenir → detectar → diagnosticar → corrigir → validar → monitorar → prevenir novamente.**
