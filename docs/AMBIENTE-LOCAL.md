# Ambiente local de desenvolvimento (MySQL + configuração)

Script: [`scripts/configurar-ambiente-local.ps1`](../scripts/configurar-ambiente-local.ps1) — Windows PowerShell 5.1
ou PowerShell 7+. Prepara **somente Development**; Produção exige cofre gerenciado (`Vault:Modo=KeyVault`, ver
[`SEGREDOS.md`](SEGREDOS.md) e [`DEPLOY.md`](DEPLOY.md)) e o código recusa o provedor local (fail-fast).

## Uso

```powershell
# Primeira vez (pede a senha do root do MySQL; cria base, contas e aplica as migrations do catálogo)
.\scripts\configurar-ambiente-local.ps1 -AplicarMigracoesCatalogo

# Reexecução: mantém as chaves criptográficas e ROTACIONA as senhas das contas da aplicação
.\scripts\configurar-ambiente-local.ps1

# Só regravar a configuração (não toca no MySQL; reaproveita as senhas já gravadas)
.\scripts\configurar-ambiente-local.ps1 -PularCriacaoBanco

# Primeiro login: contribuinte + usuário com senha; MFA opcional (ver "Primeiro acesso" abaixo)
.\scripts\criar-acesso-local.ps1

dotnet run --project src/PortalBase.Web      # Development pelo launchSettings
```

Se a política de execução bloquear o script: `powershell -ExecutionPolicy Bypass -File .\scripts\configurar-ambiente-local.ps1`.
O cliente `mysql` (8.x) é procurado no `PATH` e em `C:\Program Files\MySQL`; ou informe `-CaminhoClienteMySql`.
No Windows PowerShell 5.1 a conta administrativa é pedida numa janela de credencial do Windows (usuário `root`
preenchido). As migrations usam o `dotnet-ef` **local** fixado em `.config/dotnet-tools.json` (o script executa
`dotnet tool restore`; não é preciso ferramenta global).

### Codificação do arquivo (obrigatório: UTF-8 **com BOM**)

O Windows PowerShell 5.1 lê um `.ps1` **sem BOM** na página ANSI do sistema (1252), não em UTF-8. A primeira versão
do script foi publicada sem BOM e falhava no parse (`UnexpectedCharactersAfterHereStringHeader`, `Unexpected token
'caracteres'`, acentos como `DiretÃ³rios`): o travessão (`E2 80 94`) lido em 1252 termina em `”`, que o PowerShell
trata como aspas. Correção: arquivo em UTF-8 com BOM e sem caracteres cujos bytes viram aspas em 1252. O
`.editorconfig` fixa `charset = utf-8-bom` para `*.ps1`, e `ScriptsPowerShellCodificacaoTests` falha o build se
algum `.ps1` perder o BOM ou voltar a conter esses bytes. Ao editar, mantenha "UTF-8 with BOM" no editor.

## Primeiro acesso (login, senha e MFA opcional)

**Não existe usuário nem senha padrão** (nenhuma credencial fica em código, `appsettings` ou migration — regra 7). O
primeiro acesso é criado pelo comando de desenvolvimento [`tools/PortalBase.AcessoLocal`](../tools/PortalBase.AcessoLocal);
use o atalho [`scripts/criar-acesso-local.ps1`](../scripts/criar-acesso-local.ps1) (mesmos requisitos de codificação
acima) depois do `configurar-ambiente-local.ps1 -AplicarMigracoesCatalogo`, com o MySQL no ar:

```powershell
.\scripts\criar-acesso-local.ps1 -Cnpj <CNPJ> -Tenant STAKE -RazaoSocial 'Stake Ltda' -Login admin.local -Nome 'Admin Local' -Email admin.local@example.com
```

`-Tenant` é o **nome do tenant**, escolhido por você: é o que aparece na seleção de contribuinte e no topo do portal
(sempre em **maiúsculas** — `stake` vira `STAKE`) e define a base do contribuinte: `STAKE` → `portalbase_tenant_stake`.
Letras sem acento, dígitos e `_` (2 a 40), começando por letra. É pedido só ao **criar** o contribuinte e não muda
depois (a base tem esse nome). Um nome já usado por outro CNPJ é recusado sem gravar nada.

O que faltar é perguntado no terminal. Em ordem, o comando:

1. **Recusa** (código 3) tudo que não for desenvolvimento local: ambiente diferente de `Development` (ou ambíguo entre
   `DOTNET_ENVIRONMENT` e `ASPNETCORE_ENVIRONMENT`), `Vault:Modo` diferente de `ConfiguracaoLocal`, catálogo ou
   `BancoTenant:Servidor` fora desta máquina (só `localhost`/`127.0.0.1`/`::1`). Lê a **mesma** configuração da Web
   (appsettings da Web + user-secrets `portalbase-web` + variáveis de ambiente).
2. **Valida tudo antes de gravar**: CNPJ (dígitos verificadores), nome do tenant (formato, já usado por outro tenant,
   base `portalbase_tenant_<nome>` que sobrou com dados de **outro** CNPJ), login, razão social, nome, e-mail e a
   senha. Entrada inválida termina com código 2 e **nada** é gravado.
3. **Contribuinte**: reaproveitado pelo CNPJ ou criado (CNPJ, nome do tenant e razão social), com a base
   `portalbase_tenant_<nome>` **provisionada de verdade** (criação, migrations e grants pelo mesmo provisionador da
   aplicação) e **ativado**. Contribuinte desabilitado é recusado (a ferramenta não reativa).
4. **Usuário**: a senha é digitada **sem eco** e confirmada — nunca é argumento (ficaria no histórico). Política:
   12 a 128 caracteres, 3 de 4 tipos (minúsculas, maiúsculas, dígitos, símbolos), sem repetir demais e sem conter o
   login. No banco fica só o hash.
5. **Vínculo e papéis** (`-Papel`, padrão `Administrador`; aceita `Leitor`, `Analista`, `Administrador`).
   O Administrador tem todas as permissões do contribuinte e, no acesso local, também recebe o perfil global
   **Administrador da plataforma**: acesso total a **todos** os contribuintes e à plataforma (backup/restore, incidentes,
   e-mail SMTP, marca…), **sem pedir MFA**. Quem já tinha o acesso criado antes desse perfil: rode o mesmo comando de novo
   (é idempotente) e entre outra vez no portal. Outros usuários e perfis personalizados se cadastram pela própria tela
   (Administração → Usuários / Perfis).
   Vínculo revogado é recusado (a ferramenta não reconcede).
6. **MFA (TOTP) — opcional, desligado por padrão**: sem opção nenhuma o usuário fica **sem MFA** e o login pede só a
   senha. Com `-ComMfa` o MFA é cadastrado no terminal: a chave aparece **uma única vez** (cadastre-a no app
   autenticador em "inserir chave manualmente"), o comando pede o código de 6 dígitos e mostra os **códigos de
   recuperação**, também uma única vez. `-RemoverMfa` desliga o MFA de um usuário que já o tem (apaga o segredo e os
   códigos de recuperação e encerra as sessões abertas). Sem MFA, a política do servidor nega apenas as operações de
   alto impacto (`Permissoes.ExigemMfa`; no template, o exemplo é `dados.excluir`).

Depois: `dotnet run --project src/PortalBase.Web`, abra `http://localhost:5251/conta/entrar`, informe o **e-mail** (o do
`-Email`; maiúsculas e minúsculas tanto faz) e a senha (e o código do app, se cadastrou o MFA). O `-Login` é o nome de usuário
interno que identifica o usuário no comando; o resumo do comando mostra com que e-mail entrar. O e-mail não pode ser o de
outro usuário, e o de um usuário que já existe não muda pelo comando. Com um único contribuinte vinculado, o portal já entra nele; com vários, escolha na tela seguinte
(e troque depois pelo botão "Trocar" da barra superior). Para dar acesso a outra empresa, rode o comando de novo com
o outro `-Cnpj` e o mesmo `-Login`.

| Situação | Como |
|---|---|
| Rodar de novo | Idempotente: reaproveita o que existe, completa o que faltar e **mantém** a senha. |
| Esqueci a senha | Pelo portal: "Esqueci minha senha" na tela de login (o link chega pelo e-mail configurado — veja abaixo). Pelo terminal: `.\scripts\criar-acesso-local.ps1 -Cnpj <CNPJ> -Login admin.local -RedefinirSenha` |
| Trocar a senha | No portal, clique no seu usuário (canto superior direito) → **Minha conta** → Alterar senha. |
| Outro usuário / papel | Rode com outro `-Login` (e `-Papel`); papéis que faltam são concedidos, os existentes não mudam. |
| Cadastrar MFA | `-ComMfa` (opcional; o login passa a pedir o código do app). |
| Desligar MFA | `.\scripts\criar-acesso-local.ps1 -Cnpj <CNPJ> -Login admin.local -RemoverMfa` (o login volta a pedir só a senha). |
| Perdi o autenticador | Use um código de recuperação no login, ou desligue o MFA com `-RemoverMfa`. |
| Automação | `$senha \| .\scripts\criar-acesso-local.ps1 ... -SenhaStdin` (a senha só pela pipeline; o atalho a envia ao comando em Base64 dos bytes UTF-8, imune ao `$OutputEncoding` ASCII do 5.1 e à página de código). |
| "Recusado ... ASPNETCORE_ENVIRONMENT" / "ambíguo" | Há `ASPNETCORE_ENVIRONMENT` (ex.: `Production`) na sessão — em geral herdada das variáveis do Windows. O atalho recusa **antes** de compilar (código 3) e diz de onde ela vem (só a sessão, usuário ou máquina). Nesta janela: `Remove-Item Env:ASPNETCORE_ENVIRONMENT`; de vez (usuário): `[Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $null, 'User')` e abra outro terminal (de máquina: o mesmo com `'Machine'`, como administrador). O `configurar-ambiente-local.ps1` também avisa essa variável em "Conflitos de configuração". |

Códigos de saída: `0` sucesso, `1` falha (ex.: MySQL fora do ar, MFA pedido com `-ComMfa` e não confirmado), `2` entrada inválida, `3` recusado
pela trava de ambiente. Cada alteração é **auditada** na trilha do catálogo com o ator `acesso-local:<usuário do
sistema>`, origem `AcessoLocal` e uma correlação por execução (exibida no resumo); os eventos do MFA saem com a
mesma correlação. A senha, o segredo TOTP e os códigos de recuperação nunca vão para log.

A ferramenta não é publicada nas imagens (os Dockerfiles publicam só Web/Worker) e nenhum projeto de `src/` a
referencia — guardas em `GuardaPadroesProibidosTests`. Fora do desenvolvimento local, acessos são criados pelos fluxos
administrativos autorizados e auditados da aplicação.

## E-mail de disparo ("Esqueci a senha")

Na primeira configuração, o `configurar-ambiente-local.ps1` deixa o e-mail no modo **PastaLocal**: nada é enviado de
verdade — cada e-mail vira um arquivo `.eml` em `<DiretorioDados>\emails` (padrão
`%LOCALAPPDATA%\PortalBase\emails`), que abre no Outlook ou num editor de texto (o link de redefinição está no corpo).

Para enviar de verdade, informe o servidor SMTP (a senha é pedida sem eco, ou vem de `-SenhaSmtp`, e fica só como
segredo — `Segredos:email-smtp-senha`):

```powershell
.\scripts\configurar-ambiente-local.ps1 -PularCriacaoBanco -EmailModo Smtp `
    -SmtpServidor smtp.office365.com -SmtpPorta 587 -SmtpSeguranca StartTls `
    -SmtpUsuario nao-responda@empresa.com.br -EmailRemetente nao-responda@empresa.com.br
```

| Parâmetro | O que é |
|---|---|
| `-EmailModo` | `PastaLocal`, `Smtp` ou `Desligado`. Sem ele, uma configuração já gravada é mantida (no modo `PastaLocal`, a pasta passa a ser `<DiretorioDados>\emails`; `-UrlPublicaPortal`, se informado, é aplicado). Trocar de modo apaga as chaves do modo anterior (inclusive a senha do SMTP). |
| `-SmtpSeguranca` | `StartTls` (padrão, porta 587), `SslTls` (porta 465) ou `Nenhuma` (só servidor local de teste; recusado fora de Development). Com TLS, o envio **falha** se o servidor não oferecer TLS ou o certificado não for confiável — nunca cai para texto puro. |
| `-SmtpUsuario` | Usuário da autenticação (vazio = relay sem autenticação). |
| `-EmailRemetente` / `-EmailRemetenteNome` | Endereço/nome do remetente (precisa ser aceito pelo servidor). |
| `-UrlPublicaPortal` | Base dos links (padrão `http://localhost:5251`, atendido pelos perfis http e https do Visual Studio). |

Só a Web recebe a configuração de e-mail (o Worker não dispara e-mails). Configuração inválida impede a Web de subir,
com a lista do que corrigir. Falhas de envio aparecem no log da Web sem destinatário nem conteúdo
(`E-mail <id> NÃO enviado após N tentativa(s)`).

## O que é gravado

Destino padrão: **user-secrets** dos projetos `portalbase-web` e `portalbase-worker`
(`%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`, arquivo restrito ao usuário; carregado só em Development).
Alternativa (Windows): `-Destino VariaveisUsuario` (variáveis do USUÁRIO — valem para todos os apps .NET dele).
Nunca variável de MÁQUINA, `appsettings`, repositório ou log; nenhuma senha/chave é exibida.

| Chave | Valor |
|---|---|
| `ConnectionStrings:Catalogo` | conta de catálogo, `SslMode=Required` |
| `BancoTenant:*` | servidor, porta, `SslMode`, usuários runtime/admin e as **referências** `banco-tenant-runtime`/`banco-tenant-admin` |
| `Segredos:banco-tenant-runtime`, `Segredos:banco-tenant-admin` | senhas geradas (provedor local de Dev/Test) |
| `Segredos:identidade-mfa`, `Segredos:identidade-hash`, `Segredos:auditoria-integridade` | chaves de 32 bytes (Base64), **iguais** no Web e no Worker e **preservadas** nas reexecuções |
| `Vault:Modo=ConfiguracaoLocal`, `Vault:Ambiente=dev` | provedor local |
| `Armazenamento:DiretorioRaiz`/`DiretorioTemporario`, `DataProtection:CaminhoChaves` (Web) | diretórios privados em `%LOCALAPPDATA%\PortalBase` |

Trocar qualquer chave criptográfica invalida dados já gravados (segredos TOTP cifrados, tokens, cadeia de
integridade da auditoria); por isso só `-RotacionarChavesCriptograficas` as substitui.

## Contas MySQL (menor privilégio)

| Conta | Privilégios | Motivo |
|---|---|---|
| `portalbase_catalogo` | `SELECT, INSERT, UPDATE, DELETE` só na base de catálogo | a aplicação não faz DDL no catálogo (migrations são aplicadas à parte) |
| `portalbase_app` (`'%'`) | nenhum inicial | o provisionamento concede DML por base de tenant a `'<usuario>'@'%'`; nunca enxerga o catálogo |
| `portalbase_admin` | DDL + `GRANT OPTION` em `` `portalbase_tenant_%` `` (e `` `tenant_%` ``, bases antigas) | cria a base do tenant, aplica as migrations e concede DML ao runtime |
| `portalbase_migrador` | todos os privilégios **só** na base de catálogo (sem `GRANT OPTION`) | aplica as migrations do catálogo e, só em Development, permite à Web recriar o catálogo apagado; gravada apenas para a Web (`ConnectionStrings:CatalogoMigracao`) |

Todas com `REQUIRE SSL` (exceto com `-SslMode Disabled -PermitirSemTls`, só para MySQL local de teste). A conta
administrativa informada (ex.: root) é usada apenas durante a execução e nunca gravada.

Os padrões do admin (`` `portalbase_tenant_%` `` e `` `tenant_%` ``) vão **sem** escapar o `_`: no MySQL 8.4, o
`GRANT OPTION` concedido via `` `tenant\_%` `` permite `CREATE DATABASE` e DDL, mas o servidor **nega** o
`GRANT ... TO runtime` do provisionamento (erro 1044). Consequência aceita: o `_` casa qualquer caractere — por
isso o script recusa uma base de catálogo que caia em um desses padrões (`portalbase?tenant?*`, `tenant?*`). Quem
configurou o ambiente antes do nome escolhido precisa rodar o `configurar-ambiente-local.ps1` de novo (sem isso o
admin não cria `portalbase_tenant_*` e o provisionamento falha com acesso negado). Com
`partial_revokes=ON` o padrão não funciona e o script aborta.

Ao final, o script **prova** o modelo: login das quatro contas com as novas senhas, runtime e admin sem acesso ao
catálogo, migrador sem poder criar nenhuma base fora do catálogo, e a cadeia do provisionamento (admin cria uma base `portalbase_tenant_verificacao_*`, concede DML
ao runtime, o runtime a enxerga; a base e o grant são removidos em seguida).

## Catálogo apagado por engano (Development)

Se a base `portalbase_catalogo` (ou as suas tabelas) sumir, a **Web em Development** a recria no startup
(`GarantiaCatalogoDesenvolvimento`): cria a base com `utf8mb4` / `utf8mb4_0900_as_cs` (igual ao script) e aplica
todas as migrations com a conta migradora; o log registra `Garantia do catálogo (Development): ... base criada e N
migration(s) aplicada(s)`. O `criar-acesso-local.ps1` faz o mesmo antes de criar o acesso. As contas e os grants
continuam valendo (o MySQL guarda os privilégios por nome de base), mas o conteúdo é **novo**: contribuintes e
usuários precisam ser criados de novo (`.\scripts\criar-acesso-local.ps1`). As bases `portalbase_tenant_*` continuam
no MySQL com os dados e a marca do dono (CNPJ): recriando o tenant com o **mesmo nome e o mesmo CNPJ**, a base é
reaproveitada (aviso no terminal, auditado `TenantDatabaseReused`); com **outro** CNPJ ela é recusada — um contribuinte
nunca herda os dados de outro. Bases antigas `tenant_<guid>` (antes do nome escolhido) ficam órfãs.

Requisito: a conta migradora, criada pelo `configurar-ambiente-local.ps1` (versões anteriores do script não a
criavam — rode `.\scripts\configurar-ambiente-local.ps1 -AplicarMigracoesCatalogo` uma vez). Sem ela, a Web só
registra o erro com o comando a rodar. **Produção nunca recria o catálogo**: o preflight recusa
`ConnectionStrings:CatalogoMigracao` (fail-fast) e as migrations seguem só pelo pipeline — um catálogo apagado em
produção é incidente (restaurar backup), não algo a recriar vazio em silêncio.

## Validação

**Windows PowerShell 5.1 (CI)** — o workflow `script-ambiente-windows.yml` (runner `windows-latest`) executa
[`scripts/ci/validar-configurar-ambiente-local.ps1`](../scripts/ci/validar-configurar-ambiente-local.ps1): sobe um
MySQL 8.0 descartável (o da imagem do runner), roda o script como o usuário roda (processo `powershell.exe` novo
lendo o arquivo do disco) e verifica o parse no 5.1 (com controle negativo do defeito de BOM), user-secrets do Web e
do Worker, **ACL NTFS** (só usuário + SYSTEM, sem herança), privilégios reais das três contas e recusa sem TLS,
migrations do catálogo, a Web lendo a configuração (`/health/ready` = 200), reexecução (chaves preservadas, senhas
rotacionadas), `-PularCriacaoBanco`, parâmetros inválidos e nenhum segredo na saída nem nos logs da Web. O script é
executado duas vezes: pelo Windows PowerShell 5.1 e pelo PowerShell 7. Execução de referência: run `37040797958`
(Windows PowerShell 5.1.26100 e PowerShell 7.6.6, MySQL 8.0.46 com TLS, página ANSI 1252) — **37/37 verificações em
cada shell**; nessa máquina o próprio carregador do 5.1 também recusa um `.ps1` UTF-8 sem BOM com travessão (o
defeito original), e o script corrigido é aceito.

**PowerShell 7.6 em Linux (MySQL 8.4)** — primeira execução do zero com migrations; 13 verificações independentes
de privilégio (incluindo as migrations **reais** do tenant aplicadas pela conta admin e recusa sem TLS);
reexecução; `-PularCriacaoBanco` reaproveitando a configuração e com `-SenhaConta*`; falha clara sem console;
8 parâmetros inválidos recusados sem gravar nada; Web com `/health/ready` = 200 após cada cenário; nenhum segredo
na saída do script nem nos logs da aplicação.

Não validado: o destino `-Destino VariaveisUsuario` (o CI usa o padrão, user-secrets). No PowerShell 7.6 em
terminal **Linux**, prompts seguros consecutivos travam; o script pede no máximo um por vez quando há configuração
ou parâmetros, e falha com mensagem clara em sessão não interativa.

**Comando de acesso local** — `AcessoLocalIntegracaoTests` (MySQL real, DI de produção): criação completa com o nome
escolhido (informado em minúsculas, gravado `LOCAL_…` e base `portalbase_tenant_local_…` marcada com o dono), nome já
usado por outro CNPJ recusado sem gravar, base que sobrou de outro CNPJ recusada e a do mesmo CNPJ reaproveitada, login pelo caso de uso real (senha certa autentica, errada não), código de recuperação aceito
pelo `GestaoMfa`, autorização server-side (`admin.usuarios` com MFA), auditoria com ator/origem/correlação únicas;
reexecução sem duplicar nem auditar; `-RedefinirSenha`; entrada inválida (senha fraca, e-mail inválido, nome ausente,
confirmação divergente, nome do tenant ausente ou inválido) sem gravar **nada**; contribuinte desabilitado e vínculo revogado recusados; MFA errado 3x e
reexecução que conclui; papéis concedidos só os pedidos. Em toda execução dos testes a saída do terminal é
verificada contra as senhas usadas. `AcessoLocalTravasTests`: travas de ambiente/configuração (inclusive ordem: fora de
Development nem a configuração é lida), argumentos (senha nunca aceita nem ecoada), política de senha. Mutações
conferidas (gravar antes de validar, sem origem de auditoria, ecoar a senha do stdin, sem trava de loopback, ler
configuração antes da trava de ambiente, reconceder vínculo revogado, papel por número): todas derrubam os testes.

Execução real em Linux (PowerShell 7.6, MySQL 8.4): comando interativo num **PTY** com senha sem eco (ausente da
transcrição) e MFA confirmado com o TOTP calculado da chave exibida; o atalho `.ps1` com senha acentuada pela
pipeline; e **login HTTP na Web** (antiforgery + cookie) com os três acessos: `POST /conta/login` → `/conta/verificar`
→ `POST /conta/mfa` → `/conta/selecionar` → `POST /conta/contribuinte` → Dashboard `200`; senha errada →
`/conta/entrar?erro=1`; nenhuma senha nem a chave TOTP nos logs da Web. No CI Windows (5.1 e 7) o harness executa o
atalho de verdade, a reexecução, a recusa da senha por parâmetro e o mesmo login HTTP. A 1ª execução no Windows
(run `37066932998`) criou o acesso no 5.1, mas o login com a senha **acentuada** enviada pela pipeline falhou: a senha
gravada não conferiu. Desde então o atalho envia a senha em Base64 (`--senha-stdin-base64`, UTF-8 estrito no comando:
byte inválido é recusado, nunca vira `?`) e o harness confere também a codificação do formulário de login. Run
`37068571985` (Windows PowerShell 5.1.26100 e PowerShell 7.6.6, MySQL 8.0.46 com TLS, página ANSI 1252): **41/41
verificações em cada shell**, inclusive o login HTTP até o Dashboard com a senha acentuada. Como o lado do login já
codificava certo (`%C3%A1`), a causa da falha anterior foi confirmada: o pipe do 5.1 para o `dotnet` alterava o acento.

Regressão registrada: os runs `37094558446` e `37095691791` falharam em **1** verificação (o login HTTP): o harness ainda
esperava a tela `/conta/selecionar` depois do login, mas desde que o servidor entra direto quando há um único
contribuinte, o `POST /conta/login` responde `302 /`. O harness agora exige `302 /`, confere no Dashboard o **nome do
tenant** no topo (`-Tenant STAKE_CI` → `STAKE_CI`, base `portalbase_tenant_stake_ci`) e a recusa (código 2, nada
gravado) de um segundo CNPJ pedindo o mesmo nome. Run `37097380596` (Windows PowerShell 5.1.26100 e PowerShell 7.6.6,
MySQL 8.0.46 com TLS): **47/47 verificações em cada shell**, com `POST /conta/login` → `302 /` e Dashboard com
`STAKE_CI` no topo. Limitação do harness: quando o 5.1 executa outro `powershell.exe` com a saída capturada, a linha
`[erro] …` que o comando escreve em stderr chega como "Cannot process the XML from the 'Error' stream" (o 5.1 espera
CLIXML nesse fluxo). Por isso, nas recusas, o harness confere o **código de saída** (e, no nome repetido, o aviso
`nada foi gravado`, que vai para stdout), e não o texto do `[erro]`. Que nada é gravado é provado no banco pelos
`AcessoLocalIntegracaoTests`. Num console sem redirecionamento a mensagem aparece como foi escrita, e no PowerShell 7 o
texto também é capturado.

Windows marcado como `Production`: num Windows real, o atalho foi recusado por "ambiente ambíguo" porque havia
`ASPNETCORE_ENVIRONMENT=Production` nas variáveis do sistema e o launchSettings do comando define
`DOTNET_ENVIRONMENT=Development`. A trava continua a mesma, mas agora o atalho recusa antes de compilar, com código 3,
diz de onde a variável vem e como removê-la. O comando, quando chamado direto, informa qual variável remover, e o
`configurar-ambiente-local.ps1` avisa sobre ela em "Conflitos de configuração". O harness Windows confere a recusa (código 3,
correção na saída, base `portalbase_tenant_ambiente_ci` não criada) nos dois shells.

Correção encontrada nesse caminho: a tela `/conta/verificar` dizia aceitar código de recuperação, mas o campo tinha
`pattern="[0-9]*"` e `maxlength="8"` — o **navegador** bloqueava os códigos `XXXXX-XXXXX` exibidos na ativação (o
servidor sempre os aceitou). O campo agora aceita letras, dígitos e hífen; o E2E de navegador
`Codigo_De_Recuperacao_E_Aceito_Pela_Tela_E_Vale_Uma_Unica_Vez` falhava antes da correção (envio bloqueado) e passa
depois, provando também o uso único.

## Worker em Development

`dotnet run --project src/PortalBase.Worker` inicia em Development com a configuração gravada pelo script
(`TrabalhadorHeartbeat` ativo; `TrabalhadorBackup` só agenda backups com `Backup:AgendamentoHabilitado=true`). A
composição do host fica em `ComposicaoWorker`, e `ComposicaoWorkerTests` constrói o host do Worker em Development
(com `ValidateOnBuild`/`ValidateScopes`): um serviço registrado sem a sua dependência impede o Worker de iniciar e
derruba o teste. Ao acrescentar trabalhadores do seu domínio, registre-os em `ComposicaoWorker`.

## Remover

Apague `%APPDATA%\Microsoft\UserSecrets\portalbase-web` e `...\portalbase-worker` (ou as variáveis do usuário) e
`%LOCALAPPDATA%\PortalBase`; no MySQL: `DROP USER 'portalbase_catalogo'@'%', 'portalbase_app'@'%', 'portalbase_admin'@'%';`.
