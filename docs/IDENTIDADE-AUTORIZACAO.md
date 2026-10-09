# Identidade, Autenticação, Autorização, RBAC e MFA (Prompt 04)

Como o Portal Base autentica usuários, decide autorização **no servidor**, aplica
papéis/permissões por contribuinte e exige **segundo fator** para operações de
alto impacto. A regra soberana (seção 42): **segurança existe no servidor** — esconder
um botão, uma página ou uma URL nunca concede acesso.

> Princípios: papéis **Leitor, Analista, Administrador** (mais perfis personalizados por
> contribuinte); acesso a vários contribuintes com **troca explícita de contexto**;
> **segundo fator obrigatório para operações de alto impacto**; **autorização verificada
> no servidor, nunca só na interface**.

## Credencial de usuário × credencial de integração (não misturar)

Este documento trata da autenticação **usuário → portal** (e-mail + senha + MFA, sessão por
cookie). Credenciais de integrações externas que o seu domínio introduzir (ex.: `client_secret`
de uma API, senha de um ERP) **não são** credenciais de usuário: ficam no cofre, por tenant
(`SegredoIntegracao` — ver [SEGREDOS.md](SEGREDOS.md)), e nunca autenticam uma pessoa.

## Decisão de arquitetura (Identity)

Não havia ASP.NET Core Identity no repositório. Optou-se por **reusar a primitiva de
hash vetada** (`PasswordHasher<T>` de `Microsoft.Extensions.Identity.Core`,
PBKDF2-HMAC-SHA512, formato versionado) **sem adotar o schema/UI do Identity**: o
`Usuario` permanece um **agregado do domínio** (Clean Architecture — o Domain não conhece
ASP.NET). Nenhum algoritmo de hash/cripto próprio (seção 5).

- **Senha:** `PasswordHasher` (PBKDF2). Salt por senha, custo versionado, re-hash automático.
- **Segredo MFA em repouso:** **AES-256-GCM** (AEAD) com chave no cofre — o banco guarda
  `nonce | tag | ciphertext`, nunca o segredo em claro.
- **TOTP:** **RFC 6238** sobre HMAC-SHA1 (o padrão dos autenticadores) — norma IETF sobre
  primitiva do framework, não cripto própria.
- **Tokens/códigos de uso único:** hash **determinístico** HMAC-SHA256 (chave do cofre)
  para permitir busca por igualdade; o valor em claro nunca é persistido.

## Autenticação e sessão

- Login (`AutenticarUsuario`) pelo **e-mail**: o e-mail é guardado e buscado na forma canônica
  (`Usuario.NormalizarEmail`: sem espaços nas pontas, em minúsculas — o catálogo compara texto
  sensível a caixa); é único (`ux_usuario_email`). O nome de usuário interno (`Login`) não autentica.
  Verifica a senha, aplica **lockout** por tentativas e responde de forma **genérica** (mesma
  exceção para e-mail desconhecido, senha incorreta ou conta bloqueada — anti-enumeração,
  seções 7/26). Nunca registra a senha nem o e-mail digitado.
- Sessão por **cookie** `HttpOnly`, `SameSite=Lax`, `Secure` em produção, expiração de 8h
  com renovação deslizante (seções 8/31). Nada sensível vai para o estado do componente Blazor.
- **Revogação** (seção 28): cada usuário tem um `SecurityStamp` rotacionado em toda mudança
  sensível (senha, MFA, status, revogação de acesso). O
  `RevalidatingServerAuthenticationStateProvider` recompara o carimbo do cookie a cada 5 min;
  divergência **invalida a sessão** mesmo com o circuito SignalR aberto. Nas requisições HTTP
  (páginas e POSTs), `ValidacaoSessaoCookie` (evento `OnValidatePrincipal` do cookie) confere
  stamp, status e bloqueio no catálogo no máximo a cada `Seguranca:Sessao:RevalidacaoSegundos`
  (padrão 60; 0 = toda requisição): senha trocada/redefinida, usuário inativo ou bloqueado
  encerram as outras sessões em até esse prazo. Falha de consulta ⇒ sessão recusada.

## Senha: troca, "Esqueci a senha" e política

- **Política única** (`PoliticaSenha`, Application): 12 a 128 caracteres, 3 dos 4 tipos
  (minúsculas, maiúsculas, dígitos, símbolos), variedade mínima, sem conter o nome de usuário
  nem a parte local do e-mail. Usada na troca, na redefinição e no comando de acesso local.
- **Troca pelo próprio usuário** (`AlterarSenha`, tela **Minha conta** — clique no usuário na
  barra superior): o usuário vem do cookie autenticado, nunca do formulário; exige a senha
  atual (errar conta como tentativa: o mesmo lockout do login); rotaciona o `SecurityStamp`;
  a sessão atual é reemitida com o novo stamp e as demais caem pela revalidação acima.
  Auditoria `PasswordChanged` (permitido/negado), sem senha.
- **Esqueci a senha** (`RecuperacaoSenha`, telas `/conta/esqueci-senha` e
  `/conta/redefinir-senha`): o pedido responde sempre igual (exista ou não o e-mail); para um
  usuário ativo gera um código de alta entropia guardado **só como HMAC**
  (`token_recuperacao`), válido por 30 min, de uso único, no máximo 3 por usuário por hora, e
  enfileira o e-mail para o endereço **cadastrado**. O link usa `Email:UrlPublicaPortal`
  (configurado — nunca o cabeçalho Host). Ao abrir o link, a página guarda o código num
  cookie protegido (Data Protection, HttpOnly, SameSite=Strict, `/conta`) e segue em um
  endereço **sem** o código. A redefinição consome o código de forma **atômica** no banco
  (`UPDATE ... WHERE UsadoEmUtc IS NULL`: dois envios simultâneos ⇒ só um vale), aplica a
  política, limpa o lockout, rotaciona o stamp (encerra as sessões) e invalida os outros links
  abertos. Auditoria `PasswordRecoveryStarted`/`PasswordRecoveryCompleted`; código, link,
  senha e e-mail nunca vão para log. Rate limit por origem
  (`Seguranca:RateLimit:RecuperacaoSenhaPorJanela`, padrão 5 a cada 5 min).
- **Logs**: o `EnriquecedorRedacaoLogs` (Serilog) mascara `token=`, `codigo=`, `senha=`,
  `password=` e `code=` em qualquer propriedade de log (ex.: a URL registrada pelo log de
  requisições do ASP.NET).
- **E-mail de disparo**: seção `Email` (`Modo` = `Smtp` | `PastaLocal` (só Development/Testing) |
  `Desligado`); SMTP com `StartTls` (padrão, falha se o servidor não oferecer TLS — nunca cai
  para texto puro) ou `SslTls`, certificado validado, timeout, até 3 tentativas com espera
  exponencial em segundo plano (`ProcessadorFilaEmail`); a senha do SMTP só no cofre
  (`Email:Smtp:SenhaRef`). Configuração inválida falha o startup. Com `Desligado`, a tela de
  "Esqueci a senha" orienta a procurar o administrador.

## Papéis e permissões (RBAC)

Papéis (catálogo, seed determinístico) e o mapa **papel → permissão** aplicado:

| Papel | Permissões |
|---|---|
| **Leitor** | `dados.visualizar` |
| **Analista** | leitura + `dados.cadastrar/editar/importar` |
| **Administrador** | **acesso total no contribuinte**: todas as permissões de tenant (`PermissaoConhecida`): `dados.visualizar/cadastrar/editar/excluir/importar`, `admin.tenant`, `admin.usuarios`, `auditoria.visualizar` (`dados.excluir` com MFA) |
| *Perfis personalizados* | criados por contribuinte na tela Perfis, com as permissões escolhidas (ver abaixo) |
| **Administrador da plataforma** *(perfil global)* | **tudo, em todos os contribuintes, sem MFA** (ver abaixo) |

**Administrador com acesso total:** o papel Administrador recebe todas as permissões de tenant,
derivadas do enum `PermissaoConhecida` em `CatalogoPapeisPermissoes` (uma permissão nova entra
automaticamente no seed). Duas barreiras continuam: as operações de **alto impacto**
(`Permissoes.ExigemMfa`; no template, `dados.excluir` — acrescente as operações críticas do seu
domínio) exigem **MFA satisfeito** na sessão, e as permissões de **plataforma** (globais,
cross-tenant: `plataforma.backup.executar`, `plataforma.restore.executar`,
`plataforma.tenant.provisionar`, `plataforma.marca.administrar`, `plataforma.email.administrar`,
`plataforma.administradores.gerir`, `seguranca.incidentes.gerir`) continuam fora de qualquer papel
de tenant — vêm só do perfil global **Administrador da plataforma** (abaixo) ou da allowlist
`AutorizacaoPlataforma:AdministradoresPlataforma`.
Um administrador continua sem poder conceder/alterar o próprio privilégio. As chaves de
permissão são centralizadas em `Permissoes` (Application), nunca strings soltas.

Permissões são **escopadas ao vínculo** usuário×tenant (`UsuarioTenantPapel`): um papel só
vale no tenant em que foi concedido.

### Perfis personalizados (por contribuinte)

`Papel.TenantId` nulo = papel do **sistema** (os três acima, iguais para todos e imutáveis pela tela); preenchido =
perfil **personalizado** daquele contribuinte (`GestaoPerfis`). O administrador (`admin.usuarios`) cadastra, altera
(nome, descrição e as permissões — `papel_permissao`) e exclui (só se nenhum vínculo o usar). Regras do servidor:
nome único por contribuinte e diferente dos do sistema; só permissões de **contribuinte** (as de plataforma nunca
entram); **anti-escalada** — ninguém põe num perfil (nem concede a alguém) permissão que ele mesmo não tem no
contribuinte (tentativa auditada como `PrivilegeEscalationAttempt`); perfil de outro contribuinte é tratado como
inexistente. Como as permissões são lidas do catálogo a cada verificação, a mudança vale na próxima ação.

### Administrador da plataforma (perfil global)

Marca `usuario.administrador_plataforma` (catálogo). Quem a tem: (1) recebe **todas** as permissões de plataforma
(`IAutorizacaoPlataforma`, sem segundo fator); (2) acessa **qualquer** contribuinte sem vínculo
(`AutorizacaoTenantCatalogo` — o contribuinte continua precisando estar operacional para o uso funcional) com
**todas** as permissões de contribuinte (`RepositorioUsuario.ObterPermissoesAsync`); (3) tem o **step-up de MFA
dispensado**: no login o servidor emite a claim `portalbase:dispensa_mfa` (cookie protegido), reconhecida por
`ContextoSegurancaHttp.MfaSatisfeito` e pela policy `mfa-satisfeito`, e `ServicoAutorizacao` também a respeita pelo
catálogo. É concedida/removida na tela do usuário só por outro administrador da plataforma
(`plataforma.administradores.gerir`; nunca a si mesmo), auditado como `PlatformAdminGranted/Revoked`; mudar a marca
rotaciona o `SecurityStamp` (a sessão cai e a próxima nasce com ou sem a dispensa). O comando de acesso local
(Development) marca o usuário que recebe o papel Administrador. A lista `AutorizacaoPlataforma:AdministradoresPlataforma`
da configuração continua valendo (com `ExigirMfa`) para quem não tem a marca.

## Autorização no servidor

`IServicoAutorizacao` é a decisão única (fail-closed, auditada). Para cada operação combina:

```
principal autenticado?  →  vínculo ativo com o tenant?  →  possui a permissão?  →  (se alto impacto) MFA satisfeito?
        │ não                    │ não                          │ não                        │ não
        ▼                        ▼                              ▼                            ▼
  NaoAutenticado          TenantNaoAutorizado (IDOR)        SemPermissao                MfaRequerido
```

- `AvaliarAsync` (não lança) serve para a UI esconder botões; `ExigirAsync` (lança) é a
  **decisão real** chamada pelos casos de uso. Esconder o botão nunca basta (seção 42).
- A verificação de tenant reusa `IAutorizacaoTenant` (vínculo ativo + tenant Pronto),
  fechando **IDOR**: manipular o `tenantId` para um tenant sem vínculo é negado (seção 15/35).

## Seleção e troca de tenant (seções 13/14)

O `tenantId` **nunca** vem confiável do navegador. `SelecaoTenant.SelecionarAsync` (1) invalida
a seleção anterior, (2) valida no servidor via `IResolvedorTenant` (usuário + vínculo + tenant
ativo) e (3) só então estabelece o novo — se falhar, **nenhum** tenant fica selecionado.

**Contribuinte único.** Ao fim da autenticação (senha de usuário sem MFA cadastrado, ou step-up de MFA concluído),
`EndpointsAutenticacao.ConcluirComContribuinteAsync` consulta os vínculos ATIVOS do próprio usuário: se houver
**exatamente um** e ele estiver operacional, a seleção é feita ali mesmo, com as MESMAS verificações do
`POST /conta/contribuinte` (tenant vindo da lista de vínculos, nunca do cliente; `IResolvedorTenant.ResolverAsync`
fail-closed e auditado antes de a claim entrar no cookie). Nenhum, vários, contribuinte indisponível ou recusa na
re-resolução: a tela `/conta/selecionar`, com a sessão sem contribuinte. "Trocar" (barra superior) sempre abre a
tela. Coberto por `Contribuinte_Unico_Pula_A_Selecao_E_Varios_Exigem_Escolha` (E2E de navegador, MySQL real).

**Como o contribuinte aparece.** Na seleção (combobox) e no topo do portal o contribuinte é identificado pelo **nome
do tenant** (ex.: `STAKE`), sempre em maiúsculas — não pela razão social nem pelo CNPJ. O texto é só exibição: o
formulário envia o `TenantId`, que o servidor confere contra os vínculos do usuário.

## MFA e step-up (seções 18-20)

- **Ativação:** `IniciarAtivacao` gera o segredo (protegido no cofre) e o URI otpauth (QR);
  `ConfirmarAtivacao` valida um código do autenticador, habilita o MFA e entrega os **códigos
  de recuperação** (mostrados uma única vez; persistidos só como hash, uso único).
- **Step-up:** operações de alto impacto (`Permissoes.ExigemMfa`; no template, `dados.excluir`)
  exigem MFA **satisfeito nesta sessão** — o papel **nunca** basta. Sem step-up, negado
  (`MfaRequerido`).
- Segredos e códigos MFA nunca aparecem em log nem em mensagens de erro.

## Administração e menor privilégio

- Criar usuário, conceder/revogar acesso e atribuir papel são **operações privilegiadas**:
  exigem `admin.usuarios` no tenant (nunca por binding de formulário — seção 21).
- **Cadastro pela tela** (`GestaoUsuarios`): cadastrar (senha inicial pela política, ou link de boas-vindas de 24 h
  pelo e-mail de disparo), dar acesso a um usuário existente pelo e-mail, alterar nome/e-mail, ativar/inativar,
  liberar bloqueio, enviar link de senha, definir os perfis e **excluir do contribuinte** (remove o vínculo; se a
  conta não tiver outro contribuinte, é inativada — nunca apagada, por causa da auditoria).
- **Conta compartilhada protegida:** nome, e-mail (o login) e situação são da CONTA; se ela também tiver vínculo com
  outro contribuinte (ou for administrador da plataforma), só um administrador da plataforma altera — senão o
  administrador de um contribuinte poderia trocar o e-mail de um usuário de outro e tomar a conta pelo "Esqueci a
  senha". Trocar o e-mail derruba as sessões e invalida links de senha abertos.
- **Auto-escalonamento bloqueado:** o próprio ator não concede a si mesmo acesso, papel ou
  permissão privilegiada, nem revoga/exclui/inativa a si mesmo (`AutoConcessaoNaoPermitidaException`).
- **Deny-by-default** (seção 22): sem policy que permita explicitamente, é negado. Não existe
  `AllowAll`/`BypassAuthorization`/usuário de desenvolvimento que chegue à produção (seção 23).

## Proteções do host

Antiforgery nos POSTs (seção 24); **rate limiting** por origem em login/MFA (seção 32);
returnUrl **local** (anti open-redirect, seção 25); cabeçalhos de segurança e HSTS.

## Auditoria (identidade)

Eventos registrados (sem segredos): `Login` (sucesso/negado, lockout), `MfaAtivacaoIniciada`,
`MfaAtivacaoConfirmada`, `MfaValidado` (e via recuperação),
`AcessoConcedido`, `PapelAtribuido`, `AcessoRevogado`, `UsuarioCriado`, `RecuperacaoIniciada`,
`SenhaAlterada/Redefinida`, e negações de autorização (`Autz:<permissão>`). **Nunca** são
registrados senha, código/segredo MFA, token, cookie, chave ou segredo de integração. A trilha
completa é consolidada no Prompt 05.

## Configuração

| Chave | Uso |
|---|---|
| `SegurancaIdentidade:ChaveProtecaoMfaRef` | Referência (cofre) da chave AES-256 do segredo MFA. |
| `SegurancaIdentidade:ChaveHashRef` | Referência (cofre) da chave HMAC dos tokens/códigos. |

As chaves em si (`Segredos:<ref>`, Base64 de 32 bytes) vêm do ambiente/cofre — **nunca**
versionadas.
