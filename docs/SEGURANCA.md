# Hardening de Segurança da Aplicação e Infraestrutura (Prompt 07)

Controles transversais de segurança do Portal Base. Princípio: **fail-closed** — um controle
obrigatório ausente **impede** a operação; nunca há downgrade silencioso. Este documento
descreve apenas o que está **implementado**; o que é preparação para prompts futuros é marcado
como tal.

## TLS / HTTPS (seção 4)

- `UseHttpsRedirection` redireciona HTTP→HTTPS; **HSTS** em produção com `max-age` de 1 ano e
  `includeSubDomains` (preload desligado até inscrição real do domínio).
- A **versão do TLS** (rejeitar TLS 1.0/1.1) é controlada pela terminação TLS da
  infraestrutura (reverse proxy / ingress / gateway) quando o TLS termina lá — a aplicação
  sozinha não a controla nesse caso. Documentar onde o TLS é terminado no deploy.

## Cabeçalhos de segurança (seções 5, 6)

`UseCabecalhosSeguranca` aplica em toda resposta: `X-Content-Type-Options: nosniff`,
`X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
`Cross-Origin-Opener-Policy: same-origin`, `X-Permitted-Cross-Domain-Policies: none`,
`Permissions-Policy` restritiva e a **CSP** abaixo. Respostas de `/conta` (autenticação)
recebem `Cache-Control: no-store`.

### CSP (Blazor Server real)

```
default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline';
script-src 'self'; connect-src 'self' ws: wss:; font-src 'self';
object-src 'none'; frame-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'
```

- Sem `unsafe-eval`. `script-src 'self'` (scripts próprios). `connect-src ... ws: wss:` para o
  **WebSocket do SignalR**. `style-src 'unsafe-inline'` é necessário pelos **estilos inline do
  Blazor/Bootstrap** — é a única concessão, documentada aqui; remover exigiria nonce por estilo.

## Cookies (seção 7)

Cookie de autenticação: `HttpOnly`, `Secure` em produção, `SameSite=Lax`, expiração de 8h com
renovação deslizante. Não guardam tenant, permissões, segredos, tokens ou dados do tenant — a
autoridade é sempre o servidor. O `SecurityStamp` no cookie é revalidado (Prompt 04).

## Blazor Server / SignalR (seções 8, 9)

- Toda operação sensível decide no servidor (autenticação → tenant → vínculo → autorização →
  MFA) — nunca no estado do componente/circuito (Prompt 04). Revalidação por `SecurityStamp`.
- Circuito/Hub endurecidos: `DetailedErrors=false` fora de Development (não vaza exceções),
  `MaximumReceiveMessageSize=64 KB`, `ClientTimeoutInterval` e `HandshakeTimeout` limitados.

## CORS (seção 10)

**Não habilitado**: a aplicação é Blazor Server **mesma-origem**; não há API cross-origin. Não
se usa `AllowAnyOrigin`/`AllowCredentials`. Decisão documentada — habilitar CORS só quando houver
consumidor cross-origin legítimo, com origens explícitas por ambiente.

## Antiforgery / CSRF (seção 11)

`UseAntiforgery` ativo; os endpoints de autenticação **validam o token no servidor**
(`IAntiforgery.ValidateRequestAsync`) — não dependem só de `SameSite`. POST sem token válido →
`400` (testado).

## Rate limiting (seção 12)

Limitadores por **origem (IP)** em `/conta/login`, `/conta/mfa` e `/arquivos/importar` (janela
fixa). Sem limitador global que derrube um tenant inteiro. A chave (IP) é do servidor — o cliente
não a controla. **Prompt 68:** os limites e a janela são **configuráveis** em `Seguranca:RateLimit`
(`LoginPorJanela`/`MfaPorJanela`/`UploadPorJanela`/`JanelaMinutos`), com padrões idênticos ao
anterior (10/10/30, 5 min) e **validação fail-fast** no startup (valores inválidos abortam a
composição, nunca relaxam o limite em silêncio).

## Lockout / MFA (seções 7, 18-19) — Prompt 68

- **Lockout por usuário** configurável em `Seguranca:Lockout` (`MaxTentativas`,
  `DuracaoBloqueioMinutos`), padrão conservador 5/15 min; fail-fast para valores inválidos.
- **Anti-replay de TOTP**: o step-up marca o **passo** (contador de 30s) já consumido por usuário
  e **rejeita a reapresentação** do mesmo código dentro da janela de validade (`IRegistroConsumoTotp`).
  É defesa por processo (in-memory); multi-instância exige store distribuído (diferido). Códigos de
  recuperação já eram uso único.

## Open redirect (seção 14)

`RedirecionamentoSeguro.Local` só aceita destinos **locais** (`/...`, sem `//`, `/\`, esquema
absoluto, `javascript:`/`data:`). O `returnUrl` de login/logout/MFA passa por ele (testado).

## Path traversal (seção 15)

`GuardaCaminho`: valida nomes de arquivo (rejeita `..`, separadores, raiz, UNC, controle),
combina sob um diretório base garantindo que o resultado não escape, e **gera nomes no servidor**.
É usado pelo armazenamento de uploads e de backups; todo novo upload/download deve usá-lo.

## Uploads (seção 16)

`Kestrel.Limits.MaxRequestBodySize` limita o corpo (padrão 20 MB, configurável). O fluxo completo
de upload (extensão/magic bytes/antivírus/armazenamento fora de `wwwroot`/nome gerado) está
descrito em [UPLOADS.md](UPLOADS.md).

## XML (seção 17) — preparado

`XmlSeguro.Configuracoes()` fornece `XmlReaderSettings` com `DtdProcessing.Prohibit`,
`XmlResolver=null`, sem expansão de entidades e limites de tamanho — **bloqueia XXE** (testado).
Todo processamento de XML que o seu domínio venha a introduzir deve usar estes settings.

## SSRF (seção 18) — preparado

`GuardaSsrf`: aceita só `http`/`https` e bloqueia IPs internos (loopback, RFC1918, link-local/
metadata `169.254.169.254`, CGNAT, ULA IPv6), com allowlist explícita para endpoints internos
legítimos. Nenhum cliente HTTP de saída baseado em URL controlável existe hoje; quando existir,
deve validar o destino aqui.

## HttpClient (seção 19)

Não há `HttpClient` de saída no template (integrações externas são específicas de cada domínio).
Quando implementadas, exigirão timeout/CancellationToken/retry controlado, sem registrar cabeçalhos
sensíveis, e validação SSRF do destino.

## SQL / EF Core (seção 22)

Sem SQL concatenado; sem `FromSql`/`ExecuteSql` cru na aplicação; `IgnoreQueryFilters` é **banido**
pelo analisador. O DDL de provisionamento usa identificadores validados e parâmetros.

## Erros e information disclosure (seções 23, 24)

Produção: `UseExceptionHandler("/Error")` + `ProblemDetails`; a página de erro mostra apenas um
Request ID (correlação) — sem stack trace/SQL/connection string/segredo. Cabeçalho **Server
removido** (`AddServerHeader=false`). Sem Swagger/OpenAPI, sem página de exceção de
desenvolvimento em produção.

## Health checks (seção 25)

`/health/live` e `/health/ready` retornam **apenas o status** (JSON mínimo) — sem connection
string, segredo, nome de banco ou stack trace (testado). Liveness não toca o banco.

## Cache tenant-safe (seção 26)

O cache de segredos é por chave lógica (nome do segredo já inclui o tenant) e vive só no processo;
não há cache compartilhado de dados tenant-scoped entre tenants. Respostas autenticadas não são
cacheáveis.

## Sessão / autorização (seção 27)

Expiração, renovação, logout, revogação por `SecurityStamp` (troca de senha/papel/vínculo/
status/tenant) são do Prompt 04 — não há segunda implementação.

## Endpoints administrativos (seção 28)

Provisionamento, migrations, gestão de usuários/perfis/tenants, backup/restore e consulta de
auditoria são **casos de uso autorizados no servidor** (permissão + tenant + MFA quando aplicável). Não há
endpoint HTTP anônimo de administração, de segredo ou de download de arquivo privado.

## Banco / credenciais (seção 29)

Runtime, migration e provisionamento usam credenciais **separadas** com privilégios mínimos
(Prompt 03/05: runtime só DML por base; auditoria append-only SELECT/INSERT). Connection strings
compostas de metadados + segredo do cofre; a string final nunca é registrada.

## Containers (seções 30, 31)

`Dockerfile.Web`/`Dockerfile.Worker`: multi-stage, runtime mínima, **usuário não-root**
(`USER $APP_UID`), sem segredos em `ARG`/`ENV`/`COPY`, sem PFX na imagem. Configuração e segredos
vêm do ambiente/cofre em runtime.

## CI/CD (seção 33)

`.github/workflows/ci.yml`: `permissions: contents: read` (menor privilégio), sem `echo` de
segredos, sem artefatos com segredo/PFX/dumps; build `-warnaserror`, testes e análise de pacotes
vulneráveis. Sem deploy automático de produção.

## Git / segredos (seção 34)

Sweep por senha/segredo/token/PFX/chave privada/connection string: nenhuma ocorrência de valor
real (só nomes de propriedade/parâmetro, tipos BCL, nomes de evento e a *denylist* de sanitização).
Segredos de teste são gerados em memória — nunca reais.

## Telemetria (seção 35)

OpenTelemetry (ASP.NET Core instrumentation) captura método/rota/status/duração — **não** cabeçalhos,
cookies ou corpo. Logs estruturados (Serilog) passam pela sanitização da auditoria (Prompt 05);
`Authorization`/`Cookie`/senha/token/segredo/PFX/connection string nunca são registrados.

## Limitações conhecidas

- Versão do TLS depende da terminação na infraestrutura (documentar no deploy).
- XML/HttpClient/SSRF: controles **preparados e testados**, a serem consumidos pelos módulos de
  integração que o seu domínio introduzir.
- CSP mantém `style-src 'unsafe-inline'` por dependência do Blazor/Bootstrap (nonce por estilo é
  evolução futura).
