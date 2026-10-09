# Regras Transversais Obrigatórias — Portal-Base

Este documento é a referência canônica das regras que **todo** código deste
repositório deve seguir. É um sistema **multi-tenant**: cada contribuinte
(tenant) possui seu próprio banco de dados, e uma falha de isolamento pode
vazar dados entre contribuintes. Por isso as regras abaixo são
**bloqueantes** — uma alteração que viole qualquer uma delas não deve ser
concluída, revisada como aprovada, nem mesclada.

Para cada regra: **intenção**, **padrão correto**, **antipadrão** e **como é
verificada** (analisador, teste ou revisão). Um resumo executivo está em
[`../CLAUDE.md`](../CLAUDE.md).

> Convenção de verificação: 🤖 = analisador/compilador falha o build automaticamente ·
> 🧪 = coberto por testes · 👁️ = exige atenção em revisão de código.

---

## 1. Não implementar apenas o caminho feliz
**Intenção.** Todo fluxo trata erros, nulos, coleções vazias, concorrência,
timeouts, entradas inválidas e falhas de dependências.
**Correto.** Validar pré-condições, retornar/lançar de forma explícita, cobrir
ramos de erro em testes. `Nullable` habilitado força tratar ausência.
**Antipadrão.** Assumir que a chamada externa sempre responde, que a lista tem
itens, que o `Find` nunca retorna `null`.
**Verificação.** 🤖 `Nullable=enable`, `TreatWarningsAsErrors` · 🧪 testes de
caminho não-feliz obrigatórios (regra 19) · 👁️ revisão.

## 2. Não assumir que dados recebidos do cliente são confiáveis
**Intenção.** IDs, quantidades, flags, nomes, arquivos e cabeçalhos vindos do
cliente são hostis até validação no servidor.
**Correto.** Validar/normalizar na fronteira; usar o principal autenticado (não
o corpo da requisição) para decisões de segurança.
**Antipadrão.** `db = Databases[request.TenantId]`; confiar em `isAdmin` enviado
pelo cliente; usar um `id` do corpo para carregar recurso sem checar dono.
**Verificação.** 🤖 `CA1062` (validar argumentos públicos) · 🧪 · 👁️.

## 3. Toda autorização deve ser validada no servidor
**Intenção.** A decisão de autorização acontece no servidor, sempre.
**Correto.** Políticas de autorização e checagens baseadas em recurso no
back-end; a UI apenas reflete o resultado.
**Antipadrão.** Esconder um botão no Blazor e considerar a ação "protegida".
**Verificação.** 🧪 testes de autorização · 👁️ (ver também regras 15 e 17).

## 4. Nenhum identificador de tenant recebido do cliente pode definir diretamente o banco utilizado
**Intenção.** O cliente não escolhe o banco. O tenant é resolvido no servidor a
partir do principal autenticado, através de um mapa confiável.
**Correto.** `TenantContext` é criado por uma fábrica que exige autenticação +
autorização; a connection string vem de um `ITenantStore` confiável, chaveado
pelo `TenantId` **autorizado**.
**Antipadrão.** Montar a connection string com o `tenantId` do header/corpo;
trocar de banco por um parâmetro de rota sem reautorizar.
**Verificação.** 🧪 `ResolvedorTenantTests`, `TenantIsolationTests` e
`AuthorizationIsolationTests` (tenant não autorizado é rejeitado mesmo quando
"solicitado") · 👁️.

## 5. Nenhuma operação de dado do tenant pode ocorrer sem TenantContext válido e autorizado
**Intenção.** Sem tenant autorizado, a operação falha (fail-closed).
**Correto.** As portas de entrada exigem um `TenantContext`; o resolvedor lança
`TenantContextAusenteException` quando ele não existe.
**Antipadrão.** Executar uma consulta de dados do tenant quando o contexto ainda não foi
resolvido, "só para este caso".
**Verificação.** 🧪 (resolvedor lança sem contexto) · 👁️.

## 6. Nunca utilizar conexão padrão para banco de contribuinte
**Intenção.** Não existe fallback para uma conexão "default"/compartilhada.
**Correto.** O resolvedor obtém a conexão do `ITenantStore` para o tenant
autorizado; se não houver entrada, **lança** — nunca usa um padrão.
**Antipadrão.** `?? DefaultConnection`; usar a connection string do host da
aplicação para dados de contribuinte.
**Verificação.** 🧪 (store vazio ⇒ exceção, não fallback) · 👁️.

## 7. Nunca armazenar segredos em código, appsettings, banco ou logs
**Intenção.** Senha, certificado, token, `client_secret` e chaves ficam em um
cofre de segredos, nunca versionados nem persistidos em claro.
**Correto.** `ISecretProvider` (cofre/variáveis seguras); em dev, *user-secrets*;
`.gitignore` cobre `appsettings.*.local.json`, `*.pfx`, `*.key`.
**Antipadrão.** `"Password=..."` em `appsettings.json`; commitar `.pfx`.
**Verificação.** 👁️ revisão + varredura de segredos · 🤖 `.gitignore`.

## 8. Nunca registrar dados sensíveis em logs
**Intenção.** Logs não contêm segredos, certificados, tokens nem PII sensível.
**Correto.** Logging estruturado com redação; registrar identificadores, não
conteúdo sensível.
**Antipadrão.** `logger.LogInformation("cnx {Conn}", connectionString)`;
`Console.WriteLine(token)`.
**Verificação.** 🤖 `Console.Write*` banido (`BannedSymbols.txt`) · 👁️.

## 9. Toda operação crítica deve possuir auditoria
**Intenção.** Operações críticas registram quem, o quê, quando e em qual tenant.
**Correto.** `IAuditoria.RegistrarAsync(EventoAuditoria)`; a criação de
`TenantContext` já emite auditoria de acesso.
**Antipadrão.** Alterar/excluir dado do tenant sem trilha.
**Verificação.** 🧪 (auditoria registrada na criação do contexto) · 👁️.

## 10. Toda entrada externa deve ser validada
**Intenção.** Validação nas fronteiras (API, mensageria, arquivo), com falha
segura.
**Correto.** Objetos de valor que se auto-validam (`TenantId.Criar`),
validadores explícitos; rejeitar cedo com `EntradaInvalidaException`.
**Antipadrão.** Persistir string crua vinda da rede.
**Verificação.** 🤖 `CA1062` · 🧪 (entradas inválidas rejeitadas) · 👁️.

## 11. Toda integração externa deve possuir timeout, retry controlado, cancellation token e tratamento de falhas
**Intenção.** Nenhuma chamada externa pode travar, repetir infinitamente ou
falhar silenciosamente.
**Correto.** `HttpClient` com timeout, resiliência (retry/circuit-breaker com
backoff), `CancellationToken` propagado, tratamento explícito de falha.
**Antipadrão.** `await http.GetAsync(url)` sem timeout, sem token, sem catch.
**Verificação.** 🤖 `CA2016` (propagar `CancellationToken`) · 👁️.

## 12. Toda operação que possa ser repetida deve ser idempotente
**Intenção.** Reentregas/retries não duplicam efeito (ex.: envio a um sistema externo).
**Correto.** Chave de idempotência (`IdempotencyKey`) + `IIdempotencyStore`;
efeito aplicado no máximo uma vez.
**Antipadrão.** Criar um registro a cada retry.
**Verificação.** 🧪 · 👁️.

## 13. Não utilizar SQL concatenado
**Intenção.** Eliminar SQL injection.
**Correto.** LINQ/EF, `FromSqlInterpolated`, comandos parametrizados.
**Antipadrão.** `"SELECT ... WHERE id = " + id`; `$"... {input}"` em SQL cru.
**Verificação.** 🤖 `CA2100` = **error**.

## 14. Não utilizar IgnoreQueryFilters() para contornar regras de segurança
**Intenção.** Filtros globais (isolamento de tenant) não podem ser burlados.
**Correto.** Confiar nos filtros globais; se um cenário legítimo exigir ignorá-los,
isolar atrás de abstração auditada e aprovada — nunca chamar a API direto.
**Antipadrão.** `.IgnoreQueryFilters()` em consulta de aplicação.
**Verificação.** 🤖 banido em `BannedSymbols.txt` (falha o build).

## 15. Não confiar em autorização feita somente pela interface Blazor
**Intenção.** A UI melhora a experiência; nunca é a fronteira de segurança.
**Correto.** Reaplicar toda autorização no servidor/endpoint.
**Antipadrão.** `AuthorizeView` como única proteção de uma ação sensível.
**Verificação.** 🧪 (endpoint nega sem permissão) · 👁️.

## 16. Não expor stack trace, connection string, token ou segredo ao usuário
**Intenção.** Erros ao usuário são genéricos; detalhes ficam no log seguro.
**Correto.** `ProblemDetails` sem internals; exceções de domínio com mensagens
seguras (sem connection string).
**Antipadrão.** Devolver a exceção crua/`ex.ToString()` na resposta.
**Verificação.** 👁️ + exceções de domínio revisadas · 🧪.

## 17. Não criar endpoint administrativo sem autorização explícita
**Intenção.** Endpoints administrativos exigem política de autorização explícita.
**Correto.** `[Authorize(Policy = "Admin")]`/equivalente em todo endpoint admin;
negar por padrão.
**Antipadrão.** Endpoint `/admin/...` sem atributo de autorização.
**Verificação.** 👁️ + 🧪 (acesso não autorizado é negado).

## 18. Não introduzir dependência arquitetural que viole as regras da solution
**Intenção.** Preservar a direção de dependências entre camadas.
**Correto.** Domínio ← Aplicação ← Infraestrutura/Apresentação; o domínio não
depende de infraestrutura. Novos `ProjectReference` respeitam essa direção.
**Antipadrão.** Domínio referenciando EF Core/HTTP; ciclos entre camadas.
**Verificação.** 👁️ + (recomendado) teste de arquitetura.

## 19. Todo código novo deve possuir testes compatíveis com seu risco
**Intenção.** Cobertura proporcional ao risco; segurança/isolamento têm o teste
mais rigoroso, incluindo caminhos não-felizes.
**Correto.** Testes de autorização negada, tenant ausente, entrada inválida,
ausência de fallback de conexão.
**Antipadrão.** Só testar o sucesso; deixar o kernel de segurança sem teste.
**Verificação.** 🧪 `PortalBase.Application.Tests`, `PortalBase.Integration.Tests`
(isolamento e autorização contra MySQL real).

## 20. Ao finalizar, executar build + testes e reportar exatamente o resultado
**Intenção.** Nada é declarado pronto sem build + testes verdes e reporte fiel.
**Correto.**
```bash
dotnet build -warnaserror
dotnet test
```
Reportar o resultado **exatamente** (erros/avisos, testes passados/falhados). Se
não for possível executar, dizer isso — não presumir sucesso.
**Verificação.** 👁️ obrigatório em cada entrega.

---

## Como o repositório enforça (resumo)

| Mecanismo | Arquivo | Regras cobertas |
|---|---|---|
| MSBuild (nullable, warnaserror, analisadores de segurança) | `Directory.Build.props` | 1, 2, 7, 8, 11, 13, 16, 20 |
| Severidades de analisador | `.editorconfig` | 2, 8, 10, 11, 13 |
| APIs proibidas | `BannedSymbols.txt` | 8, 14, 16 |
| Kernel de segurança + testes | `src/PortalBase.Application/{Multitenancy,Autorizacao,Seguranca}`, `src/PortalBase.Infrastructure/Multitenancy`, `tests/PortalBase.Application.Tests`, `tests/PortalBase.Integration.Tests` | 1, 4, 5, 6, 9, 10, 16, 19 |
| Segredos fora do versionamento | `.gitignore` | 7 |
| Revisão de código | processo | 3, 15, 17, 18 (+ todas) |

### Ativar/estender guardrails ao criar novos projetos
- Novos projetos herdam `Directory.Build.props` automaticamente.
- Ao introduzir EF Core, mantenha os filtros globais de tenant e confirme que a
  linha de `IgnoreQueryFilters` em `BannedSymbols.txt` cobre a assinatura em uso.
- Ao introduzir chamadas HTTP, use um cliente com timeout + resiliência +
  `CancellationToken` (regra 11).
