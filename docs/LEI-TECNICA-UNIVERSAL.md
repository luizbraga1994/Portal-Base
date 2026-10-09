# LEI TÉCNICA UNIVERSAL

**Padrão Obrigatório para Desenvolvimento, Evolução e Manutenção de Sistemas**

| | |
|---|---|
| Versão | 1.0 |
| Classificação | Norma Técnica Obrigatória |
| Aplicabilidade | Todos os sistemas, projetos, APIs, aplicações web, SaaS, integrações, serviços, workers, bancos de dados e componentes desenvolvidos ou mantidos. |

> Norma fornecida pelo responsável pelo Portal Base em 08/10/2026 e adotada como obrigatória neste repositório,
> **em conjunto** com as [Regras Transversais Obrigatórias](REGRAS-TRANSVERSAIS-OBRIGATORIAS.md) (que tratam da
> segurança multi-tenant), a [Lei 02](LEI-02-AUDITORIA-RECUPERACAO-MODERNIZACAO.md) e a
> [Lei 03](LEI-03-AUDITORIA-CONTINUA.md) — no ciclo das três leis, esta é a **Lei 01 — Desenvolver corretamente**. Em
> caso de dúvida, vale a exigência mais rígida. O texto abaixo é o original; nada foi acrescentado às regras.

## 1. Princípio fundamental

Todo sistema deverá ser desenvolvido e mantido considerando simultaneamente:

- Funcionalidade;
- Performance;
- Escalabilidade;
- Segurança;
- Disponibilidade;
- Integridade dos dados;
- Observabilidade;
- Manutenibilidade;
- Resiliência;
- Recuperação de desastres;
- Evolução futura.

É proibido considerar uma implementação concluída apenas porque a funcionalidade solicitada está funcionando.

Toda implementação deverá ser avaliada também pelo impacto que poderá causar no sistema atual e no crescimento futuro.

## 2. Regra de não regressão

Nenhuma alteração poderá:

- degradar uma funcionalidade existente;
- aumentar significativamente o consumo de CPU sem justificativa;
- aumentar significativamente o consumo de memória sem justificativa;
- aumentar desnecessariamente o número de consultas ao banco;
- introduzir N+1 queries;
- remover índices necessários;
- quebrar paginação;
- aumentar significativamente o tempo de resposta;
- criar gargalos;
- criar pontos únicos de falha desnecessários;
- comprometer backup ou recuperação;
- comprometer segurança;
- introduzir dependências desnecessárias.

Toda alteração deverá considerar seus efeitos colaterais.

## 3. Regra de volumetria

Nenhum desenvolvimento poderá assumir que a quantidade de dados será pequena.

Toda implementação deverá considerar crescimento.

Uma consulta que funciona com:

```
100 registros
```

deverá ser analisada considerando:

```
10.000 registros
100.000 registros
1.000.000 registros
10.000.000+ registros
```

quando aplicável ao domínio.

"Funciona com poucos dados" não é critério suficiente de aceitação.

## 4. Regra de paginação

Listagens nunca deverão carregar quantidade ilimitada de registros.

Toda listagem deverá possuir:

- paginação;
- limite máximo;
- ordenação determinística;
- filtros adequados;
- quantidade padrão de registros;
- controle de tamanho máximo da página.

Para grandes volumes deverá ser avaliado o uso de:

- Cursor Pagination;
- Keyset Pagination.

É proibido utilizar consultas que retornem grandes volumes de dados desnecessariamente.

## 5. Regra de projeção

Consultas deverão retornar somente os dados necessários.

É proibido utilizar:

```
SELECT *
```

em consultas críticas ou de grande volume quando uma projeção específica for possível.

O backend deverá utilizar DTOs/projeções adequadas sempre que necessário.

## 6. Regra contra N+1

Nenhuma funcionalidade crítica poderá introduzir N+1 queries.

Toda consulta deverá ser analisada considerando:

```
Quantidade de registros
+
Quantidade de queries executadas
+
Tempo total
+
Consumo de memória
```

Uma implementação que executa:

```
1 query principal
+
N queries adicionais
```

deverá ser considerada suspeita e investigada.

## 7. Regra de banco de dados

Toda consulta relevante deverá ser avaliada quanto a:

- índices;
- cardinalidade;
- plano de execução;
- filtros;
- ordenação;
- joins;
- locks;
- concorrência;
- volume retornado;
- custo computacional.

Não deverão ser criados índices indiscriminadamente.

Também não deverão ser removidos índices sem análise de impacto.

## 8. Regra de consultas pesadas

Consultas potencialmente pesadas deverão possuir proteção.

Exemplos:

- limite de período;
- paginação;
- filtros obrigatórios;
- execução assíncrona;
- processamento em background;
- cache;
- materialização;
- agregações pré-processadas.

Consultas capazes de retornar milhões de registros não deverão ser executadas diretamente em uma requisição HTTP comum
sem justificativa arquitetural.

## 9. Regra de exportação

Exportações grandes deverão ser tratadas como processamento assíncrono.

Fluxo recomendado:

```
Solicitação
    ↓
Job
    ↓
Fila
    ↓
Worker
    ↓
Processamento
    ↓
Arquivo
    ↓
Download
```

É proibido manter uma requisição HTTP aberta por longos períodos para processamento que possa ser executado em
background.

## 10. Regra de API

Toda API deverá considerar:

- timeout;
- paginação;
- rate limiting;
- validação;
- autenticação;
- autorização;
- tratamento de erros;
- idempotência quando aplicável;
- observabilidade;
- controle de payload;
- limites de requisição.

Endpoints não deverão aceitar payloads ilimitados.

## 11. Regra de frontend

O frontend não deverá carregar dados desnecessários.

Deverá ser avaliado:

- lazy loading;
- carregamento sob demanda;
- paginação;
- debounce;
- cache;
- compressão;
- bundle size;
- imagens;
- chamadas duplicadas;
- chamadas simultâneas desnecessárias.

Uma página não deverá carregar todos os módulos e dados possíveis apenas porque eles existem.

## 12. Regra de cache

Sempre que aplicável deverá ser avaliada a utilização de cache para dados:

- frequentemente consultados;
- pouco alterados;
- custosos para obter;
- compartilhados entre requisições.

Em ambientes distribuídos deverá ser avaliado o uso de cache distribuído.

O cache não poderá ser utilizado para mascarar uma arquitetura ou consulta inadequada.

## 13. Regra de escalabilidade

Sempre que houver necessidade de múltiplas instâncias, a aplicação deverá ser preferencialmente stateless.

Não deverá existir dependência desnecessária de:

- memória local;
- sessão local;
- arquivos locais;
- estado local da instância.

A arquitetura deverá permitir escalabilidade horizontal quando necessário.

## 14. Regra de load balancer

Quando houver múltiplas instâncias, deverá existir mecanismo adequado de distribuição de carga.

Deverão ser considerados:

- Health Check;
- Load Balancing;
- Failover;
- Timeout;
- Connection Draining;
- distribuição uniforme;
- remoção automática de instâncias indisponíveis.

Uma instância com problema não deverá continuar recebendo tráfego normalmente.

## 15. Regra de health check

Deverão ser diferenciados:

- **Liveness** — verifica se a aplicação está viva.
- **Readiness** — verifica se a aplicação está apta a receber tráfego.

Exemplo:

```
/health/live
/health/ready
```

Dependências críticas deverão ser consideradas no readiness conforme a arquitetura.

## 16. Regra de processamento assíncrono

Processamentos demorados não deverão bloquear requisições HTTP desnecessariamente.

Deverão ser avaliados:

- filas;
- workers;
- mensageria;
- jobs;
- retry;
- dead-letter queue;
- idempotência.

Exemplos:

- envio de e-mail;
- processamentos em lote;
- importações;
- exportações;
- integrações;
- geração de relatórios;
- processamento de arquivos.

## 17. Regra de integrações externas

Toda integração externa deverá possuir proteção contra indisponibilidade.

Deverão ser considerados:

```
Timeout
Retry
Backoff
Circuit Breaker
Rate Limit
Idempotência
Logs
Monitoramento
```

É proibido permitir que uma API externa indisponível cause bloqueio generalizado da aplicação.

## 18. Regra de transações

Transações deverão ser mantidas pelo menor tempo necessário.

É proibido manter transações abertas enquanto:

- aguarda API externa;
- aguarda usuário;
- aguarda fila;
- executa processamento demorado.

Operações externas não deverão permanecer dentro de transações de banco sem justificativa técnica.

## 19. Regra de concorrência

Operações concorrentes deverão possuir estratégia adequada.

Deverá ser avaliado:

- Optimistic Concurrency;
- RowVersion;
- locks;
- transações;
- idempotência;
- distributed locks.

Atualizações concorrentes não poderão causar perda silenciosa de dados.

## 20. Regra de conexões

Conexões com banco e recursos externos deverão ser corretamente gerenciados.

Deverão ser monitorados:

- connection pool;
- quantidade de conexões;
- timeout;
- conexões presas;
- conexões excessivas.

É proibido abrir conexões sem necessidade ou mantê-las abertas durante processamento que não exige conexão.

## 21. Regra de observabilidade

Todo sistema deverá possuir observabilidade adequada.

Deverão ser considerados:

**Logs**

- aplicação;
- erros;
- segurança;
- auditoria;
- integrações;
- processamento.

**Métricas**

- CPU;
- memória;
- latência;
- throughput;
- erros;
- banco;
- filas;
- cache.

**Tracing** — quando aplicável:

```
Request
 ↓
API
 ↓
Database
 ↓
Queue
 ↓
Worker
 ↓
External API
```

## 22. Regra de logs

Logs deverão possuir contexto suficiente para investigação.

Sempre que aplicável deverão conter:

- CorrelationId;
- RequestId;
- TenantId;
- UserId;
- operação;
- serviço;
- timestamp;
- duração;
- resultado;
- erro.

Dados sensíveis não deverão ser registrados.

## 23. Regra de segurança

Toda funcionalidade deverá considerar:

- autenticação;
- autorização;
- RBAC/ABAC quando aplicável;
- criptografia;
- secrets management;
- validação de entrada;
- proteção contra injection;
- rate limiting;
- auditoria;
- controle de sessão;
- proteção de arquivos.

Segurança não deverá ser adicionada somente ao final do desenvolvimento.

## 24. Regra de multi-tenancy

Em sistemas SaaS, toda operação deverá respeitar isolamento entre tenants.

Nenhuma consulta poderá depender exclusivamente da camada de apresentação para garantir isolamento.

O TenantId deverá ser tratado como requisito arquitetural.

Deverá ser impossível que `Tenant A` acesse dados de `Tenant B` por manipulação de parâmetros.

## 25. Regra de arquivos

Arquivos grandes não deverão ser armazenados no banco sem justificativa.

Deverá ser avaliado:

- Object Storage;
- S3;
- Azure Blob;
- MinIO;
- storage dedicado.

O banco deverá armazenar metadados quando apropriado.

## 26. Regra de upload

Uploads deverão possuir:

- limite de tamanho;
- validação de extensão;
- validação de MIME type;
- nome seguro;
- armazenamento seguro;
- timeout;
- proteção contra arquivos maliciosos;
- controle de quantidade.

## 27. Regra de backup

Todo sistema que possua dados persistentes deverá possuir estratégia formal de backup.

Deverão ser definidos:

- **RPO** — quantidade máxima aceitável de perda de dados.
- **RTO** — tempo máximo aceitável para recuperação.

Também deverão ser definidos:

- frequência;
- retenção;
- criptografia;
- armazenamento externo;
- cópias redundantes;
- monitoramento.

## 28. Regra de restore

Backup somente será considerado válido quando sua restauração puder ser comprovada.

Deverão existir testes periódicos de:

```
Backup
 ↓
Restore
 ↓
Validação
 ↓
Sistema funcional
```

## 29. Regra de disaster recovery

Sistemas críticos deverão possuir plano de recuperação.

Deverão ser documentados:

- cenário de falha;
- infraestrutura alternativa;
- procedimento de restauração;
- responsáveis;
- dependências;
- tempo estimado;
- validação pós-restauração.

## 30. Regra de retenção

Dados deverão possuir política de retenção.

Deverá ser definido:

```
Dados operacionais
Logs
Auditoria
Arquivos
Eventos
Backups
```

Não deverá haver crescimento infinito de tabelas sem estratégia.

## 31. Regra de arquivamento

Dados históricos poderão ser transferidos para estruturas de arquivo quando isso melhorar:

- performance;
- manutenção;
- custo;
- backup;
- consultas operacionais.

## 32. Regra de testes

Toda funcionalidade deverá possuir testes compatíveis com sua criticidade.

Quando aplicável:

```
Unit Tests
Integration Tests
Contract Tests
API Tests
Security Tests
Performance Tests
Load Tests
Stress Tests
End-to-End Tests
```

## 33. Regra de teste de carga

Sistemas críticos deverão ser submetidos a testes de carga.

Deverão ser avaliados:

- throughput;
- latência;
- CPU;
- memória;
- banco;
- conexões;
- filas;
- cache;
- erros.

Deverá ser identificado o ponto de saturação do sistema.

## 34. Regra de performance baseada em métricas

Performance deverá ser medida por métricas.

Sempre que possível utilizar:

```
P50
P95
P99
Throughput
Error Rate
```

Exemplo:

```
P95 API < 500ms
P99 API < 2s
Error Rate < 1%
```

Os valores deverão ser definidos de acordo com o sistema.

## 35. Regra de deploy

Deploy deverá possuir:

- CI/CD;
- testes automatizados;
- validação;
- migrations controladas;
- rollback;
- logs;
- monitoramento pós-deploy.

Alterações críticas deverão possuir estratégia de rollback.

## 36. Regra de migração de banco

Toda migration deverá ser analisada quanto a:

- duração;
- locks;
- tamanho;
- impacto em produção;
- compatibilidade;
- rollback;
- downtime.

Migrations potencialmente destrutivas deverão exigir análise adicional.

## 37. Regra de código

Código deverá priorizar:

- simplicidade;
- legibilidade;
- coesão;
- baixo acoplamento;
- testabilidade;
- manutenibilidade.

Complexidade desnecessária deverá ser evitada.

## 38. Regra contra hardcode

Valores configuráveis não deverão ser hardcoded.

Deverão ser avaliados:

```
Configuration
Environment Variables
Secrets
Feature Flags
Database Configuration
```

## 39. Regra de configuração

Configurações deverão ser separadas por ambiente:

```
Development
Testing
Staging
Production
```

Credenciais de produção jamais deverão estar no código-fonte.

## 40. Regra de dependências

Toda nova biblioteca deverá ser avaliada quanto a:

- segurança;
- manutenção;
- licença;
- performance;
- compatibilidade;
- comunidade;
- necessidade real.

Não adicionar dependências simplesmente porque facilitam uma pequena implementação.

## 41. Regra de manutenção

Toda manutenção deverá começar com análise do comportamento atual.

Antes de modificar:

```
Código
Banco
API
Frontend
Integrações
Workers
```

deverá ser identificado:

- fluxo atual;
- dependências;
- impactos;
- riscos;
- testes existentes.

## 42. Regra de impacto

Toda alteração deverá responder:

```
1. O que será alterado?
2. Quem depende disso?
3. Qual o impacto no banco?
4. Qual o impacto na API?
5. Qual o impacto no frontend?
6. Qual o impacto na performance?
7. Qual o impacto na segurança?
8. Qual o impacto na escalabilidade?
9. Qual o impacto no backup?
10. Como será validado?
```

## 43. Regra de regressão

Antes de considerar uma alteração concluída deverão ser executados testes de regressão adequados.

Uma correção não poderá criar outro problema em uma funcionalidade existente.

## 44. Regra de documentação

Decisões arquiteturais relevantes deverão ser documentadas.

Quando necessário utilizar ADR (Architecture Decision Record).

Deverão ser documentadas decisões como:

- banco;
- cache;
- mensageria;
- multi-tenancy;
- particionamento;
- estratégia de backup;
- escalabilidade;
- integrações;
- segurança.

## 45. Regra de monitoramento pós-deploy

Após alterações relevantes deverá ser observado:

```
CPU
RAM
Latency
Errors
Database
Queues
Cache
Logs
```

durante período adequado.

## 46. Regra de alertas

Sistemas críticos deverão possuir alertas para eventos relevantes.

Exemplos:

```
CPU elevada
Memória elevada
Banco saturado
Fila crescendo
Erro elevado
API lenta
Disco cheio
Backup falhou
Serviço indisponível
Certificado próximo do vencimento
```

## 47. Regra de capacidade

Todo sistema crítico deverá possuir uma estimativa de capacidade.

Deverá ser possível responder:

- Quantos usuários, requisições e registros o sistema suporta atualmente?
- Qual é o próximo gargalo esperado?

## 48. Regra de gargalo

Quando identificado um gargalo, não deverá ser aplicada apenas uma otimização superficial.

Deverá ser identificado:

```
Causa raiz
 ↓
Métrica
 ↓
Impacto
 ↓
Correção
 ↓
Validação
```

## 49. Regra de root cause

Correções deverão buscar a causa raiz.

É proibido considerar concluído um problema apenas porque o sintoma desapareceu temporariamente.

## 50. Regra final — Definition of Done

Nenhuma tarefa poderá ser considerada concluída somente porque: `"Funciona."`

Uma tarefa estará concluída quando:

```
✓ Funcionalidade implementada
✓ Testes realizados
✓ Regressão validada
✓ Performance analisada
✓ Banco analisado
✓ Paginação validada
✓ Segurança validada
✓ Logs adequados
✓ Observabilidade adequada
✓ Impactos avaliados
✓ Escalabilidade considerada
✓ Backup/Restore preservados
✓ Documentação atualizada quando necessário
✓ Deploy validado
```

## 51. Comando obrigatório para qualquer implementação

Ao receber uma solicitação de desenvolvimento ou manutenção, antes de executar a alteração, realizar obrigatoriamente:

**Etapa 1 — Entender.** Identificar: objetivo; regras de negócio; funcionalidades afetadas; usuários afetados; dados
envolvidos.

**Etapa 2 — Analisar.** Investigar: arquitetura; código existente; banco; APIs; integrações; dependências;
performance; segurança.

**Etapa 3 — Identificar riscos.** Listar: riscos funcionais; riscos de performance; riscos de dados; riscos de
segurança; riscos de escalabilidade; riscos de disponibilidade.

**Etapa 4 — Planejar.** Definir: solução; arquivos afetados; banco; migrations; testes; observabilidade; rollback.

**Etapa 5 — Implementar.** Implementar respeitando todas as regras desta norma.

**Etapa 6 — Validar.** Executar: testes; análise de performance; análise de banco; análise de logs; análise de
regressão.

**Etapa 7 — Auditar.** Antes de concluir, verificar novamente se a alteração:

```
Não criou gargalo.
Não criou N+1.
Não removeu paginação.
Não aumentou consultas desnecessariamente.
Não criou dependência de estado local.
Não comprometeu segurança.
Não comprometeu backup.
Não comprometeu escalabilidade.
Não criou regressão.
```

**Etapa 8 — Concluir.** Somente declarar a tarefa concluída quando todos os critérios aplicáveis tiverem sido
atendidos.

## 52. Princípio supremo

**TODO SISTEMA DEVE SER PROJETADO PARA CRESCER, NÃO APENAS PARA FUNCIONAR.**

Uma solução que funciona hoje, mas quebra quando o volume aumenta, não é considerada uma solução adequada.

Uma solução que resolve uma funcionalidade, mas cria um gargalo, não é considerada concluída.

Uma solução que funciona, mas não pode ser monitorada, recuperada ou mantida adequadamente, não está pronta para
produção.

Esta norma deverá ser considerada obrigatória em todo desenvolvimento, manutenção, correção, refatoração, integração ou
evolução do sistema.
