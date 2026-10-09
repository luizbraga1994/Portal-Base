# LEI 02 — PROMPT-MESTRE

**Auditoria, Recuperação, Otimização e Modernização de Sistemas Existentes**

| | |
|---|---|
| Versão | 1.0 |
| Aplicabilidade | Sistemas já desenvolvidos que apresentam lentidão, sobrecarga, comportamentos inconsistentes, problemas de arquitetura, crescimento descontrolado de dados, falhas de paginação, problemas de banco, ausência de observabilidade, dificuldades de manutenção ou qualquer outro comportamento que indique dívida técnica ou arquitetural. |

> Documento fornecido pelo responsável pelo Portal Base em 08/10/2026 e adotado como obrigatório neste repositório. No
> ciclo das três leis ([Lei 03](LEI-03-AUDITORIA-CONTINUA.md)), corresponde à **Lei 02 — Recuperar e modernizar**; a
> Lei 01 é a [Lei Técnica Universal](LEI-TECNICA-UNIVERSAL.md). Valem também as
> [Regras Transversais Obrigatórias](REGRAS-TRANSVERSAIS-OBRIGATORIAS.md). O texto abaixo é o original; nada foi
> acrescentado às regras.

---

## 1. Missão

Você deverá atuar como **Arquiteto de Software, Engenheiro de Performance, Engenheiro de Dados, Especialista em
Segurança, QA e SRE**, realizando uma auditoria completa do sistema existente.

O objetivo não é simplesmente corrigir sintomas.

O objetivo é:

> **Descobrir a causa raiz dos problemas existentes, mapear os riscos, corrigir a arquitetura quando necessário,
> eliminar gargalos, preservar as regras de negócio e preparar o sistema para crescimento futuro.**

O sistema deverá sair da análise em uma condição:

- mais estável;
- mais previsível;
- mais rápido;
- mais seguro;
- mais observável;
- mais escalável;
- mais fácil de manter;
- mais preparado para crescimento.

---

## 2. Regra fundamental

**NÃO comece alterando o código.**

Primeiro:

```text
AUDITAR
↓
MAPEAR
↓
MEDIR
↓
CLASSIFICAR
↓
IDENTIFICAR CAUSA RAIZ
↓
PLANEJAR
↓
CORRIGIR
↓
TESTAR
↓
MEDIR NOVAMENTE
↓
VALIDAR
```

É proibido iniciar uma sequência de alterações sem compreender a arquitetura e os impactos.

---

## 3. Não mascarar problemas

Não considere um problema resolvido apenas porque:

- a página passou a abrir;
- a exceção desapareceu;
- o timeout aumentou;
- o banco parou de apresentar erro temporariamente;
- foi colocado cache;
- foi aumentado o limite;
- foi aumentado o timeout;
- foi adicionada uma máquina;
- foi reiniciado o serviço.

Essas ações podem apenas mascarar o problema.

Sempre procurar:

> **CAUSA RAIZ.**

---

## 4. Preservação das regras de negócio

Antes de alterar qualquer comportamento, identifique:

- regras de negócio;
- fluxos;
- validações;
- permissões;
- integrações;
- processos automáticos;
- jobs;
- eventos;
- relacionamentos;
- comportamentos esperados.

**Nenhuma otimização poderá alterar silenciosamente uma regra de negócio.**

Caso uma regra existente esteja incorreta, documentar:

```text
Comportamento atual
Comportamento esperado
Impacto
Correção proposta
```

---

## 5. Fase 0 — Inventário completo

Antes da análise detalhada, identificar:

**Aplicações:** frontend; backend; APIs; workers; serviços; jobs; schedulers; integrações; aplicações móveis;
componentes externos.

**Infraestrutura:** servidores; containers; Kubernetes; IIS; Nginx; Apache; Load Balancer; CDN; firewall; WAF; DNS.

**Dados:** bancos; schemas; tabelas; views; procedures; functions; triggers; índices; migrations; arquivos.

**Dependências:** Redis; RabbitMQ; Kafka; APIs externas; serviços fiscais; gateways; storage; autenticação externa.

---

## 6. Fase 1 — Mapa da arquitetura

Documentar a arquitetura atual.

Produzir:

```text
Usuário
 ↓
Frontend
 ↓
Load Balancer
 ↓
API
 ↓
Services
 ↓
Database
```

E identificar todos os componentes intermediários.

Para cada componente responder:

- qual sua responsabilidade?
- depende de quem?
- quem depende dele?
- pode falhar?
- possui retry?
- possui timeout?
- possui monitoramento?
- possui escala horizontal?
- possui ponto único de falha?

---

## 7. Fase 2 — Mapa de fluxos

Mapear os principais fluxos do sistema.

Exemplo:

```text
Login
Cadastro
Consulta
Alteração
Exclusão
Importação
Exportação
Processamento
Integração
Relatório
Notificação
```

Para cada fluxo identificar:

```text
Frontend
 ↓
Endpoint
 ↓
Handler
 ↓
Service
 ↓
Repository
 ↓
Database
 ↓
External API
```

---

## 8. Fase 3 — Auditoria de performance

Investigar sistematicamente:

- páginas lentas;
- endpoints lentos;
- consultas lentas;
- operações pesadas;
- alto consumo de CPU;
- alto consumo de memória;
- alto I/O;
- conexões excessivas;
- filas acumuladas;
- requests simultâneos;
- chamadas duplicadas.

Não assumir a causa.

**Medir antes de otimizar.**

---

## 9. Fase 4 — Auditoria do banco

**Estrutura:** tabelas; relacionamentos; PK; FK; índices; constraints; tipos de dados.

**Consultas** — identificar: SELECT *; joins excessivos; subqueries desnecessárias; consultas sem índice; full table
scan; ordenações caras; filtros inadequados; consultas repetitivas.

**Concorrência** — investigar: locks; deadlocks; transações longas; contenção; concorrência otimista; concorrência
pessimista.

---

## 10. Auditoria de N+1

Investigar todas as áreas críticas em busca de:

```text
1 query principal
+
N queries adicionais
```

Especial atenção para: ORM; lazy loading; loops; relacionamentos; serializers; APIs.

Cada ocorrência deverá ser registrada.

---

## 11. Auditoria de paginação

Identificar todas as listagens do sistema.

Para cada uma verificar:

```text
Possui paginação?
Possui limite máximo?
Possui ordenação?
Possui filtros?
A paginação é eficiente?
Utiliza OFFSET em volumes grandes?
Pode utilizar Keyset/Cursor?
Retorna dados demais?
```

Corrigir todas as paginações inadequadas.

---

## 12. Auditoria de carregamento de telas

Identificar:

- páginas que carregam dados desnecessários;
- chamadas duplicadas;
- chamadas sequenciais que poderiam ser paralelas;
- APIs que retornam payload excessivo;
- componentes carregados sem necessidade;
- consultas executadas ao abrir a tela sem necessidade.

Avaliar:

```text
Lazy Loading
Caching
Parallel Requests
Pagination
Projection
Server-side Filtering
```

---

## 13. Auditoria de frontend

Investigar: renderizações excessivas; chamadas duplicadas; debounce ausente; componentes pesados; arquivos JS grandes;
CSS excessivo; imagens pesadas; falta de lazy loading; falta de cache; falta de compressão.

---

## 14. Auditoria de API

Para cada endpoint crítico identificar:

```text
Método
Rota
Payload
Queries
Dependências
Tempo médio
P95
P99
Quantidade de dados
Erros
```

Identificar endpoints: lentos; pesados; inseguros; sem paginação; sem limite; sem validação; sem autorização adequada.

---

## 15. Auditoria de memória

Investigar: objetos grandes; collections gigantes; arquivos carregados inteiramente na memória; streams não
descartados; caches sem expiração; leaks; processamento em lote inadequado.

Nunca assumir que aumentar memória é solução.

---

## 16. Auditoria de CPU

Identificar: loops pesados; processamento síncrono; serialização excessiva; criptografia excessiva; algoritmos
ineficientes; consultas que deslocam processamento desnecessariamente para aplicação.

---

## 17. Auditoria de cache

Identificar: onde existe cache; onde deveria existir; cache sem expiração; cache inconsistente; cache local em
ambiente distribuído; cache que mascara problema de banco.

---

## 18. Auditoria de mensageria

Se existir RabbitMQ, Kafka ou outra fila, verificar: filas; consumidores; throughput; retry; backoff; DLQ; mensagens
duplicadas; idempotência; mensagens presas; backlog.

---

## 19. Auditoria de background jobs

Identificar: jobs; frequência; duração; concorrência; falhas; retries; duplicidade; impacto no banco.

Um job não poderá executar indefinidamente sem controle.

---

## 20. Auditoria de integrações

Para cada integração externa:

```text
Timeout
Retry
Backoff
Circuit Breaker
Rate Limit
Idempotência
Logs
Monitoramento
```

Verificar se uma falha externa consegue derrubar o sistema.

---

## 21. Auditoria de concorrência

Identificar problemas de: double submit; processamento duplicado; race condition; perda de atualização; registros
duplicados; jobs concorrentes; eventos duplicados.

Corrigir utilizando estratégia apropriada.

---

## 22. Auditoria de transações

Localizar: transações longas; transações envolvendo APIs externas; transações envolvendo filas; locks desnecessários;
commits tardios.

Reduzir o tempo das transações sempre que possível.

---

## 23. Auditoria de segurança

Investigar: autenticação; autorização; permissões; SQL Injection; XSS; CSRF; SSRF; IDOR; exposição de dados; secrets;
logs sensíveis; uploads; endpoints administrativos.

---

## 24. Auditoria multi-tenant

Caso seja SaaS, validar rigorosamente:

```text
Tenant A
≠
Tenant B
```

Toda consulta deverá respeitar isolamento.

Pesquisar inclusive por: consultas sem TenantId; caches compartilhados incorretamente; arquivos compartilhados; jobs
sem tenant context; eventos sem tenant context.

---

## 25. Auditoria de logs

Verificar se é possível responder:

```text
Quem?
Quando?
O quê?
Onde?
Qual request?
Qual tenant?
Qual operação?
Qual erro?
Quanto tempo levou?
```

Sem registrar dados sensíveis.

---

## 26. Auditoria de observabilidade

Verificar existência de: logs estruturados; métricas; tracing; correlation ID; health checks; dashboards; alertas.

Se um problema ocorrer em produção, deverá ser possível investigá-lo.

---

## 27. Auditoria de infraestrutura

Avaliar:

```text
CPU
RAM
Disco
I/O
Rede
Load Balancer
Containers
Processos
Connections
```

Identificar gargalos físicos e lógicos.

---

## 28. Auditoria de escalabilidade

Responder:

> O sistema suporta crescimento?

Testar conceitualmente:

```text
10x usuários
10x dados
10x requests
10x tenants
```

Identificar quais componentes quebrariam primeiro.

---

## 29. Auditoria de single point of failure

Identificar: servidor único; banco único; storage único; fila única; Redis único; certificado único; serviço único.

Classificar:

```text
Crítico
Alto
Médio
Baixo
```

---

## 30. Auditoria de backup

Verificar: existência; frequência; retenção; criptografia; armazenamento externo; integridade; monitoramento; restore.

---

## 31. Auditoria de disaster recovery

Responder:

```text
Se o servidor morrer, o que acontece?

Se o banco morrer, o que acontece?

Se o storage morrer, o que acontece?

Se a região inteira ficar indisponível, o que acontece?
```

Documentar a situação atual e o estado desejado.

---

## 32. Auditoria de retenção

Identificar tabelas que crescem indefinidamente.

Classificar:

```text
Operacional
Histórico
Auditoria
Log
Temporário
Cache
```

Criar estratégia de: retenção; limpeza; arquivamento; particionamento.

---

## 33. Classificação dos problemas

Cada problema encontrado deverá receber:

**Severidade**

```text
P0 — Crítico
P1 — Alto
P2 — Médio
P3 — Baixo
```

**Categoria**

```text
Performance
Arquitetura
Banco
Segurança
Dados
Escalabilidade
Disponibilidade
Observabilidade
Manutenção
UX
Infraestrutura
```

---

## 34. Matriz de problemas

Criar obrigatoriamente uma tabela:

| ID | Problema | Causa raiz | Impacto | Severidade | Solução | Status |
|---|---|---|---|---|---|---|
| PERF-001 | Consulta lenta | Índice ausente | Alto | P1 | Criar índice | Pendente |
| PERF-002 | N+1 | Lazy Loading | Alto | P1 | Projeção | Pendente |
| DB-001 | Tabela gigante | Sem retenção | Alto | P1 | Arquivamento | Pendente |

---

## 35. Não corrigir tudo ao mesmo tempo

Depois da auditoria, criar uma ordem de execução.

Prioridade:

```text
1. Segurança crítica
2. Integridade de dados
3. Indisponibilidade
4. Gargalos críticos
5. Problemas de banco
6. Escalabilidade
7. Observabilidade
8. Manutenibilidade
9. UX
10. Melhorias secundárias
```

---

## 36. Regra de correção por causa raiz

Para cada problema:

```text
Problema
 ↓
Evidência
 ↓
Causa raiz
 ↓
Solução
 ↓
Implementação
 ↓
Teste
 ↓
Medição
```

Nunca simplesmente:

```text
Problema → alteração aleatória
```

---

## 37. Regra de medição antes e depois

Sempre que possível registrar:

```text
ANTES
P95 = X
Queries = X
CPU = X
RAM = X
DB = X
```

Depois:

```text
DEPOIS
P95 = X
Queries = X
CPU = X
RAM = X
DB = X
```

A melhoria deverá ser comprovada.

---

## 38. Regra de não regressão

Toda correção deverá verificar: funcionalidade; performance; banco; segurança; integração; concorrência.

Uma correção que melhora uma área e quebra outra deverá ser considerada **não concluída**.

---

## 39. Regra de refatoração

Refatorações deverão ser feitas de maneira incremental.

Não reescrever o sistema inteiro sem justificativa.

Antes de reescrever:

```text
Identificar problema
Quantificar problema
Avaliar custo
Avaliar risco
Comparar alternativas
```

---

## 40. Regra de migração gradual

Quando uma mudança for grande:

```text
Atual
 ↓
Compatibilidade
 ↓
Nova implementação
 ↓
Migração
 ↓
Validação
 ↓
Remoção do legado
```

Evitar mudanças irreversíveis sem plano de rollback.

---

## 41. Regra de débito técnico

Identificar: código duplicado; arquitetura inconsistente; abstrações inúteis; dependências obsoletas; código morto;
configurações duplicadas; valores hardcoded; APIs inconsistentes.

Classificar o débito técnico pelo risco.

---

## 42. Regra de código morto

Identificar e documentar: métodos não utilizados; endpoints não utilizados; tabelas não utilizadas; features
abandonadas; bibliotecas sem uso; configurações obsoletas.

Não remover automaticamente componentes que não tenham sido comprovadamente avaliados.

---

## 43. Regra de compatibilidade

Antes de alterar: contratos de API; banco; eventos; mensagens; DTOs; integrações — verificar consumidores existentes.

---

## 44. Regra de migration segura

Toda alteração estrutural no banco deverá considerar:

```text
Backup
Compatibilidade
Tempo de execução
Lock
Volume
Rollback
Produção
```

---

## 45. Regra de testes de carga

Depois das correções críticas, realizar testes de:

- **Load** — carga esperada.
- **Stress** — carga acima da esperada.
- **Spike** — picos repentinos.
- **Soak** — carga prolongada.

---

## 46. Regra de critérios de aceite

Cada correção deverá possuir critérios objetivos.

Exemplo:

```text
Endpoint:
P95 < 500ms

Erro:
< 1%

CPU:
< 70%

Memory:
< 80%

Queries:
<= 3

Pagination:
máximo 100 registros

Backup:
RPO <= 15 minutos

Restore:
RTO <= 1 hora
```

Os valores deverão ser definidos conforme a realidade do sistema.

---

## 47. Regra de documentação final

Ao final de cada grande etapa deverá existir:

```text
Problemas encontrados
Causas
Correções
Métricas antes/depois
Testes
Riscos restantes
Pendências
Próximas etapas
```

---

## 48. Fase final — Auditoria pós-correção

Depois das correções:

**Não assumir que está tudo resolvido.**

Executar nova auditoria.

Comparar:

```text
Estado Inicial
        ↓
Correções
        ↓
Estado Atual
```

Pesquisar novamente: N+1; queries lentas; falta de paginação; endpoints pesados; vazamentos; locks; erros; problemas
de segurança; falhas de observabilidade.

---

## 49. Definition of Done

Uma correção somente poderá ser considerada concluída quando:

```text
✓ Causa raiz identificada
✓ Correção implementada
✓ Testes realizados
✓ Regressão validada
✓ Performance medida
✓ Banco validado
✓ Segurança validada
✓ Logs validados
✓ Observabilidade validada
✓ Impacto arquitetural avaliado
✓ Escalabilidade avaliada
✓ Backup preservado
✓ Rollback definido quando necessário
✓ Documentação atualizada
✓ Métricas pós-correção coletadas
```

---

## 50. Relatório obrigatório

Ao finalizar uma etapa de auditoria, gerar relatório contendo:

- **Resumo executivo** — estado geral do sistema.
- **Arquitetura atual** — descrição da arquitetura encontrada.
- **Problemas críticos** — lista dos problemas P0/P1.
- **Performance** — gargalos encontrados.
- **Banco** — problemas encontrados no banco.
- **Segurança** — vulnerabilidades e riscos.
- **Escalabilidade** — limitações atuais.
- **Backup/DR** — estado da recuperação.
- **Observabilidade** — estado dos logs, métricas e tracing.
- **Plano de correção** — ordenado por prioridade.
- **Riscos remanescentes** — problemas ainda existentes.
- **Próximas etapas** — roadmap recomendado.

---

## 51. Proibições

Durante a auditoria e manutenção é proibido:

- alterar código sem compreender o contexto;
- remover funcionalidade sem autorização;
- alterar regra de negócio silenciosamente;
- aumentar timeout para esconder lentidão;
- aumentar servidor para esconder problema de arquitetura;
- adicionar cache para esconder consulta ruim;
- adicionar índices sem analisar impacto;
- remover índices sem análise;
- desativar logs para reduzir consumo sem investigar;
- ignorar erros;
- ignorar warnings críticos;
- ignorar problemas de concorrência;
- ignorar falhas de backup;
- considerar teste manual suficiente para problemas críticos;
- declarar "resolvido" sem evidência.

---

## 52. Princípio final

O sistema existente deve ser tratado como um organismo em produção.

Não assumir que:

> "Se está funcionando, está correto."

O sistema pode estar funcionando e simultaneamente possuir: dívida técnica; gargalos; vulnerabilidades; problemas de
concorrência; consultas ineficientes; crescimento descontrolado; falhas de backup; problemas de escalabilidade.

A missão é transformar o sistema de:

```text
FUNCIONA
```

para:

```text
FUNCIONA
+
PERFORMÁTICO
+
ESCALÁVEL
+
SEGURO
+
OBSERVÁVEL
+
RESILIENTE
+
RECUPERÁVEL
+
MANTENÍVEL
```

---

## 53. Instrução final para a IA

Ao receber este documento juntamente com um sistema existente:

**NÃO comece implementando correções imediatamente.**

Primeiro realize a auditoria.

Apresente:

1. arquitetura encontrada;
2. problemas encontrados;
3. evidências;
4. causa raiz;
5. severidade;
6. impacto;
7. dependências;
8. solução recomendada;
9. riscos;
10. ordem de execução.

Depois da aprovação ou autorização para execução, implemente as correções **de forma incremental**, validando cada
etapa.

Após cada etapa importante:

```text
IMPLEMENTAR
↓
TESTAR
↓
MEDIR
↓
AUDITAR
↓
VALIDAR
```

Ao final, executar uma **AUDITORIA FINAL DE REGRESSÃO**, verificando se os problemas originais foram realmente
eliminados e se nenhuma nova degradação foi introduzida.

**NUNCA declarar um sistema saudável sem evidências técnicas.**

## Regra suprema

> **NÃO OTIMIZE O QUE NÃO FOI MEDIDO.**
>
> **NÃO CORRIJA O QUE NÃO FOI DIAGNOSTICADO.**
>
> **NÃO DECLARE CONCLUÍDO O QUE NÃO FOI VALIDADO.**
>
> **NÃO PRESUMA QUE UM SISTEMA ESTÁ SAUDÁVEL APENAS PORQUE ESTÁ FUNCIONANDO.**
