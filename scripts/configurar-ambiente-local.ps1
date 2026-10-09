#Requires -Version 5.1
<#
.SYNOPSIS
    Configura o ambiente LOCAL de desenvolvimento do Portal Base (MySQL com contas de menor privilégio + configuração
    do Web e do Worker), sem gravar segredo em código, appsettings, variável de MÁQUINA, repositório ou log.

.DESCRIPTION
    Adaptação, para ESTE repositório, de um script de variáveis de ambiente de outro projeto. Diferenças deliberadas
    (CLAUDE.md, regras 3 a 8):

      * Só grava as chaves que o código realmente lê: ConnectionStrings:Catalogo, BancoTenant:*, Segredos:<ref>,
        Vault:*, SegurancaIdentidade:*, Auditoria:*, Armazenamento:* e DataProtection:CaminhoChaves. Os hosts usam
        WebApplication/Host.CreateApplicationBuilder (variáveis de ambiente SEM prefixo): um prefixo como
        "PORTALBASE_" nunca seria lido.
      * Não há JWT (a autenticação é por cookie). As chaves geradas são as 3 chaves de 32 bytes do projeto - proteção
        do segredo MFA, hash de identidade e integridade da auditoria - criadas UMA vez e preservadas nas execuções
        seguintes (trocar qualquer uma invalida dados já gravados: segredos TOTP cifrados, tokens e a cadeia de
        integridade da auditoria).
      * A aplicação NUNCA usa a conta administrativa do MySQL (root). São criadas quatro contas:
          - catálogo  : só SELECT/INSERT/UPDATE/DELETE na base de catálogo;
          - runtime   : nenhum privilégio inicial - o provisionamento concede DML por base `tenant_*`
                        (criada em '%' porque o provisionamento concede a '<usuario>'@'%');
          - admin     : DDL + GRANT OPTION somente nas bases `portalbase_tenant_%` (e `tenant_%`, legado) -
                        provisionamento/migrations dos tenants;
          - migrador  : todos os privilégios SOMENTE na base de catálogo (sem GRANT OPTION). Aplica as migrations do
                        catálogo e, só em Development, deixa a Web recriar a base/tabelas se forem apagadas
                        (ConnectionStrings:CatalogoMigracao, gravada apenas para a Web; Produção recusa essa chave).
        As senhas dessas contas são geradas aleatoriamente e nunca exibidas; a senha administrativa é pedida de forma
        interativa (ou via -CredencialAdministrativaMySql), usada só neste processo e nunca gravada.
      * TLS exigido por padrão (SslMode=Required, como o código); Disabled só com -PermitirSemTls.
      * Destino padrão: user-secrets (por projeto e por usuário do sistema; carregados só em Development). Nunca
        variável de MÁQUINA (legível por qualquer usuário da máquina).

    Produção NÃO é configurada por este script: o código exige cofre gerenciado (Vault:Modo=KeyVault) e recusa o
    provedor local (fail-fast). Ver docs/AMBIENTE-LOCAL.md, docs/DEPLOY.md e docs/SEGREDOS.md.

.PARAMETER Destino
    UserSecrets (padrão, recomendado): grava em secrets.json dos projetos portalbase-web e portalbase-worker.
    VariaveisUsuario (só Windows): variáveis de ambiente do USUÁRIO - compartilhadas por TODOS os apps .NET desse
    usuário (nomes sem prefixo podem colidir com outros projetos).

.PARAMETER CredencialAdministrativaMySql
    Conta administrativa do MySQL usada apenas para criar base/contas e (opcionalmente) aplicar as migrations do
    catálogo. Se omitida, é solicitada interativamente (usuário padrão: root).

.PARAMETER HostContas
    Host das contas de catálogo e admin (padrão '%'). A conta de runtime é sempre '%' (exigência do provisionamento).

.PARAMETER PularCriacaoBanco
    Não altera o MySQL: as três contas já existem. A senha de cada conta vem, nesta ordem, de -SenhaContaCatalogo/
    -SenhaContaRuntime/-SenhaContaAdmin, da configuração já gravada (reexecução) ou, por fim, do console.

.PARAMETER SenhaContaCatalogo
    (Só com -PularCriacaoBanco) senha atual da conta de catálogo, como SecureString. Idem -SenhaContaRuntime e
    -SenhaContaAdmin.

.PARAMETER AplicarMigracoesCatalogo
    Aplica as migrations do catálogo (dotnet ef) com a conta administrativa. O catálogo NÃO é migrado no startup.

.PARAMETER EmailModo
    E-mail de disparo (ex.: "Esqueci a senha"). PastaLocal (padrão na 1ª configuração): cada e-mail vira um arquivo
    .eml em <DiretorioDados>\emails, sem servidor. Smtp: envia pelo servidor informado em -SmtpServidor. Desligado: não
    envia (a recuperação de senha fica indisponível). Sem o parâmetro, uma configuração de e-mail já gravada é mantida
    (no modo PastaLocal, a pasta passa a ser a de -DiretorioDados; -UrlPublicaPortal, se informado, é aplicado).

.PARAMETER SmtpServidor
    (Com -EmailModo Smtp) servidor SMTP, ex.: smtp.office365.com. Idem -SmtpPorta (padrão 587), -SmtpSeguranca
    (StartTls padrão; SslTls para a porta 465; Nenhuma só para servidor local de teste) e -SmtpUsuario (vazio = sem
    autenticação). A senha do SMTP vem de -SenhaSmtp (SecureString), da configuração já gravada ou do console, e é
    gravada só como segredo (Segredos:email-smtp-senha), nunca exibida.

.PARAMETER EmailRemetente
    Endereço do remetente (ex.: nao-responda@empresa.com.br); precisa ser aceito pelo servidor SMTP. -EmailRemetenteNome
    é o nome exibido (padrão: Portal Base).

.PARAMETER UrlPublicaPortal
    Endereço do portal usado nos links dos e-mails (padrão http://localhost:5251, que o perfil http e o https do
    Visual Studio atendem).

.PARAMETER RotacionarChavesCriptograficas
    Gera novas chaves MFA/hash/auditoria mesmo que existam. DESTRUTIVO para dados já gravados - só em base descartável.

.EXAMPLE
    .\scripts\configurar-ambiente-local.ps1 -AplicarMigracoesCatalogo

.EXAMPLE
    .\scripts\configurar-ambiente-local.ps1 -PularCriacaoBanco -Destino VariaveisUsuario

.EXAMPLE
    .\scripts\configurar-ambiente-local.ps1 -PularCriacaoBanco -EmailModo Smtp -SmtpServidor smtp.office365.com -SmtpUsuario nao-responda@empresa.com.br -EmailRemetente nao-responda@empresa.com.br
#>
[CmdletBinding(PositionalBinding = $false)] # argumento solto vira erro, nunca cai em outro parâmetro
param(
    [ValidateSet('UserSecrets', 'VariaveisUsuario')]
    [string] $Destino = 'UserSecrets',

    [ValidatePattern('^[A-Za-z0-9.\-]{1,253}$')]
    [string] $ServidorMySql = 'localhost',

    [ValidateRange(1, 65535)]
    [int] $PortaMySql = 3306,

    [ValidateSet('Required', 'VerifyCA', 'VerifyFull', 'Disabled')]
    [string] $SslMode = 'Required',

    [System.Management.Automation.PSCredential]
    [System.Management.Automation.Credential()]
    $CredencialAdministrativaMySql = [System.Management.Automation.PSCredential]::Empty,

    [ValidatePattern('^[a-z][a-z0-9_]{1,63}$')]
    [string] $BaseCatalogo = 'portalbase_catalogo',

    [ValidatePattern('^[A-Za-z0-9_]{1,32}$')]
    [string] $UsuarioCatalogo = 'portalbase_catalogo',

    [ValidatePattern('^[A-Za-z0-9_]{1,32}$')]
    [string] $UsuarioRuntimeTenant = 'portalbase_app',

    [ValidatePattern('^[A-Za-z0-9_]{1,32}$')]
    [string] $UsuarioAdminTenant = 'portalbase_admin',

    [ValidatePattern('^[A-Za-z0-9_]{1,32}$')]
    [string] $UsuarioMigracaoCatalogo = 'portalbase_migrador',

    [ValidatePattern('^(%|[A-Za-z0-9.\-]{1,60})$')]
    [string] $HostContas = '%',

    [string] $CaminhoClienteMySql,

    [string] $DiretorioDados,

    [switch] $PularCriacaoBanco,

    [securestring] $SenhaContaCatalogo,

    [securestring] $SenhaContaRuntime,

    [securestring] $SenhaContaAdmin,

    [securestring] $SenhaContaMigracao,

    [switch] $AplicarMigracoesCatalogo,

    [switch] $PermitirSemTls,

    [ValidateSet('PastaLocal', 'Smtp', 'Desligado')]
    [string] $EmailModo,

    [ValidatePattern('^[A-Za-z0-9.\-]{1,253}$')]
    [string] $SmtpServidor,

    [ValidateRange(1, 65535)]
    [int] $SmtpPorta = 587,

    [ValidateSet('StartTls', 'SslTls', 'Nenhuma')]
    [string] $SmtpSeguranca = 'StartTls',

    [ValidatePattern('^[^\s;,<>]{1,256}$')]
    [string] $SmtpUsuario,

    [securestring] $SenhaSmtp,

    [ValidatePattern('^[^\s@;,<>]+@[^\s@;,<>]+\.[^\s@;,<>]+$')]
    [string] $EmailRemetente,

    [ValidatePattern('^[^\r\n<>]{1,100}$')]
    [string] $EmailRemetenteNome = 'Portal Base',

    [ValidatePattern('^https?://[A-Za-z0-9.\-]+(:[0-9]{1,5})?(/[A-Za-z0-9._~\-/]*)?$')]
    [string] $UrlPublicaPortal = 'http://localhost:5251',

    [switch] $RotacionarChavesCriptograficas
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$EhWindows = $env:OS -eq 'Windows_NT'
$RaizRepositorio = Split-Path -Parent $PSScriptRoot
$ProjetosUserSecrets = [ordered]@{ Web = 'portalbase-web'; Worker = 'portalbase-worker' }

# Referências (nomes) dos segredos - as mesmas de appsettings.json; os VALORES só vão para Segredos:<ref>.
$RefRuntime = 'banco-tenant-runtime'
$RefAdmin = 'banco-tenant-admin'
$RefSenhaSmtp = 'email-smtp-senha'
$RefsChaves = [ordered]@{
    'SegurancaIdentidade:ChaveProtecaoMfaRef' = 'identidade-mfa'
    'SegurancaIdentidade:ChaveHashRef'        = 'identidade-hash'
    'Auditoria:ChaveIntegridadeRef'           = 'auditoria-integridade'
}

function Write-Etapa([string] $Texto) { Write-Host "`n==> $Texto" -ForegroundColor Cyan }
function Write-Ok([string] $Texto) { Write-Host "    [ok] $Texto" -ForegroundColor Green }
function Write-Aviso([string] $Texto) { Write-Host "    [aviso] $Texto" -ForegroundColor Yellow }

# ------------------------------------------------------------------------------------------------ validações
# 'portalbase?tenant?*' / 'tenant?*' = os padrões MySQL `portalbase_tenant_%` e `tenant_%` (legado) da conta admin ('_' casa
# 1 caractere): o catálogo não pode cair neles.
if ($BaseCatalogo -like 'portalbase?tenant?*' -or $BaseCatalogo -like 'tenant?*' -or @('mysql', 'sys', 'information_schema', 'performance_schema') -contains $BaseCatalogo) {
    throw "BaseCatalogo '$BaseCatalogo' é reservada (o prefixo tenant pertence às bases de contribuinte)."
}
$contas = @($UsuarioCatalogo, $UsuarioRuntimeTenant, $UsuarioAdminTenant, $UsuarioMigracaoCatalogo)
if (($contas | Select-Object -Unique).Count -ne 4) {
    throw 'As contas de catálogo, runtime, admin e migrador devem ser DISTINTAS (menor privilégio; runtime != admin).'
}
if ($SslMode -eq 'Disabled' -and -not $PermitirSemTls) {
    throw 'SslMode=Disabled envia credenciais sem TLS. Use -PermitirSemTls apenas com MySQL local de teste.'
}
if ($Destino -eq 'VariaveisUsuario' -and -not $EhWindows) {
    throw 'Destino VariaveisUsuario só é suportado no Windows. Use o destino UserSecrets.'
}
if ($EmailModo -eq 'Smtp') {
    if (-not $SmtpServidor) { throw 'Com -EmailModo Smtp informe -SmtpServidor (ex.: smtp.office365.com).' }
    if (-not $EmailRemetente) { throw 'Com -EmailModo Smtp informe -EmailRemetente (o endereço que aparece como remetente).' }
}
elseif ($SmtpServidor -or $SmtpUsuario -or $SenhaSmtp) {
    throw 'Os parâmetros -Smtp*/-SenhaSmtp só valem com -EmailModo Smtp.'
}
if ($SenhaSmtp -and -not $SmtpUsuario) { throw '-SenhaSmtp exige -SmtpUsuario.' }
if (-not $DiretorioDados) {
    $DiretorioDados = if ($EhWindows) { Join-Path $env:LOCALAPPDATA 'PortalBase' } else { Join-Path $HOME '.local/share/PortalBase' }
}

# ------------------------------------------------------------------------------------------------ utilitários
function Use-Rng([scriptblock] $Bloco) {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { & $Bloco $rng } finally { $rng.Dispose() }
}

function New-ChaveBase64 {
    Use-Rng { param($rng) $bytes = New-Object byte[] 32; $rng.GetBytes($bytes); [Convert]::ToBase64String($bytes) }
}

function New-SenhaForte([int] $Tamanho = 32) {
    # Sem ; = ' " \ ` nem espaço: seguro em connection string, literal SQL, arquivo de opções e JSON.
    $alfabeto = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_.!@#%^*+'
    $limite = 256 - (256 % $alfabeto.Length) # rejeição: sem viés de módulo
    do {
        $senha = Use-Rng {
            param($rng)
            $saida = New-Object System.Text.StringBuilder
            $byte = New-Object byte[] 1
            while ($saida.Length -lt $Tamanho) {
                $rng.GetBytes($byte)
                if ($byte[0] -lt $limite) { [void] $saida.Append($alfabeto[$byte[0] % $alfabeto.Length]) }
            }
            $saida.ToString()
        }
    } until ($senha -cmatch '[A-Z]' -and $senha -cmatch '[a-z]' -and $senha -match '[0-9]' -and $senha -match '[^A-Za-z0-9]')
    $senha
}

function Assert-SessaoInterativa([string] $Motivo) {
    # Sem console interativo, Read-Host/Get-Credential não leem nada (o pwsh chega a encerrar com código 0).
    if ([Console]::IsInputRedirected -or -not [Environment]::UserInteractive) {
        throw "Sessão não interativa: $Motivo exige digitação no console. Execute o script em um terminal interativo."
    }
}

function Read-Senha([string] $Rotulo) {
    Assert-SessaoInterativa "a leitura de '$Rotulo'"
    $seguro = Read-Host -Prompt $Rotulo -AsSecureString
    $texto = (New-Object System.Management.Automation.PSCredential 'x', $seguro).GetNetworkCredential().Password
    if (-not $texto) { throw "$Rotulo não pode ser vazia." }
    $texto
}

function Set-AcessoSomenteUsuario([string] $Caminho, [switch] $Diretorio) {
    if ($EhWindows) {
        # Descritor NOVO + gravação pelo .NET (não Get-Acl/Set-Acl): só a DACL alterada é persistida. O Set-Acl do 5.1
        # tenta gravar também dono e SACL e falha sem privilégio ("not allowed to be the owner" / "SeSecurityPrivilege"),
        # sobretudo na reexecução, quando a herança já está desligada.
        $acl = if ($Diretorio) { New-Object System.Security.AccessControl.DirectorySecurity } else { New-Object System.Security.AccessControl.FileSecurity }
        $acl.SetAccessRuleProtection($true, $false) # sem herança: só as regras abaixo
        $heranca = if ($Diretorio) { 'ContainerInherit,ObjectInherit' } else { 'None' }
        $identidades = @(
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
            (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-18') # SYSTEM
        )
        foreach ($sid in $identidades) {
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $heranca, 'None', 'Allow')))
        }
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            # .NET Framework: métodos estáticos de File/Directory.
            if ($Diretorio) { [System.IO.Directory]::SetAccessControl($Caminho, $acl) } else { [System.IO.File]::SetAccessControl($Caminho, $acl) }
        }
        else {
            # .NET (PowerShell 7): os mesmos métodos viraram extensões em FileSystemAclExtensions.
            $item = if ($Diretorio) { New-Object System.IO.DirectoryInfo $Caminho } else { New-Object System.IO.FileInfo $Caminho }
            [System.IO.FileSystemAclExtensions]::SetAccessControl($item, $acl)
        }
    }
    else {
        $modo = if ($Diretorio) { '700' } else { '600' }
        & chmod $modo -- $Caminho
        if ($LASTEXITCODE -ne 0) { throw "chmod falhou para $Caminho" }
    }
}

function New-DiretorioPrivado([string] $Caminho) {
    if (-not (Test-Path -LiteralPath $Caminho)) { New-Item -ItemType Directory -Path $Caminho -Force | Out-Null }
    Set-AcessoSomenteUsuario -Caminho $Caminho -Diretorio
}

function Format-ValorConexao([string] $Valor) {
    # Connection string do MySql.Data: aspas duplas (internas duplicadas) quando há separador/aspas/espaço.
    if ($Valor -match '[;"''=]' -or $Valor -ne $Valor.Trim()) { return '"' + $Valor.Replace('"', '""') + '"' }
    $Valor
}

function New-ConnectionString([string] $Base, [string] $Usuario, [string] $Senha) {
    $partes = @(
        "Server=$ServidorMySql", "Port=$PortaMySql", "Database=$Base",
        "User Id=$(Format-ValorConexao $Usuario)", "Password=$(Format-ValorConexao $Senha)", "SslMode=$SslMode"
    )
    if ($SslMode -eq 'Disabled') { $partes += 'AllowPublicKeyRetrieval=true' } # caching_sha2 sem TLS (só teste local)
    ($partes -join ';') + ';'
}

function Format-ValorOpcaoMySql([string] $Valor) {
    $escapado = $Valor.Replace('\', '\\')
    if (-not $escapado.Contains('"')) { return '"' + $escapado + '"' }
    if (-not $escapado.Contains("'")) { return "'" + $escapado + "'" }
    throw 'A senha administrativa contém aspas simples e duplas; não é possível passá-la com segurança ao cliente mysql.'
}

function Find-ClienteMySql {
    if ($CaminhoClienteMySql) {
        if (-not (Test-Path -LiteralPath $CaminhoClienteMySql)) { throw "Cliente mysql não encontrado em '$CaminhoClienteMySql'." }
        return $CaminhoClienteMySql
    }
    $comando = Get-Command mysql -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($comando) { return $comando.Path }
    if ($EhWindows) {
        $candidato = Get-ChildItem -Path (Join-Path $env:ProgramFiles 'MySQL') -Filter mysql.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($candidato) { return $candidato.FullName }
    }
    throw 'Cliente mysql não encontrado. Instale o MySQL Shell/cliente 8.x ou informe -CaminhoClienteMySql.'
}

function Invoke-MySql([string] $Usuario, [string] $Senha, [string] $Sql) {
    # Credencial num arquivo de opções temporário e privado (nunca na linha de comando); SQL pela entrada padrão.
    $opcoes = [System.IO.Path]::GetTempFileName()
    try {
        Set-AcessoSomenteUsuario -Caminho $opcoes
        $linhas = @('[client]', "user=$Usuario", "password=$(Format-ValorOpcaoMySql $Senha)", "host=$ServidorMySql", "port=$PortaMySql")
        [System.IO.File]::WriteAllLines($opcoes, $linhas, (New-Object System.Text.UTF8Encoding $false))
        $modoCliente = @{ Required = 'REQUIRED'; VerifyCA = 'VERIFY_CA'; VerifyFull = 'VERIFY_IDENTITY'; Disabled = 'DISABLED' }[$SslMode]
        $argumentos = @("--defaults-extra-file=$opcoes", "--ssl-mode=$modoCliente", '--protocol=TCP', '--batch', '--skip-column-names')
        if ($SslMode -eq 'Disabled') { $argumentos += '--get-server-public-key' }
        $anterior = $ErrorActionPreference
        $ErrorActionPreference = 'Continue' # stderr de executável nativo não deve abortar antes de lermos o código de saída
        try { $saida = $Sql | & $script:ClienteMySql @argumentos 2>&1 } finally { $ErrorActionPreference = $anterior }
        $texto = @($saida | ForEach-Object { "$_" })
        if ($LASTEXITCODE -ne 0) { throw "mysql ($Usuario) falhou com código ${LASTEXITCODE}: $($texto -join ' | ')" }
        , $texto
    }
    finally {
        Remove-Item -LiteralPath $opcoes -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Dotnet([string[]] $Argumentos) {
    # Vale o código de saída: no Windows PowerShell 5.1 com a saída redirecionada (CI, Tee-Object), cada linha de
    # stderr vira ErrorRecord e, com ErrorActionPreference=Stop, abortaria um comando que deu certo.
    $anterior = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & dotnet @Argumentos 2>&1 | ForEach-Object { "$_" } | Out-Host } finally { $ErrorActionPreference = $anterior }
    $LASTEXITCODE
}

function Get-CaminhoUserSecrets([string] $Id) {
    $base = if ($EhWindows) { Join-Path $env:APPDATA 'Microsoft\UserSecrets' } else { Join-Path $HOME '.microsoft/usersecrets' }
    Join-Path (Join-Path $base $Id) 'secrets.json'
}

function Read-ConfiguracaoPlana([string] $Caminho) {
    $mapa = [ordered]@{}
    if (Test-Path -LiteralPath $Caminho) {
        $texto = [System.IO.File]::ReadAllText($Caminho)
        if ($texto.Trim()) {
            foreach ($propriedade in ($texto | ConvertFrom-Json).PSObject.Properties) {
                if ($null -ne $propriedade.Value -and $propriedade.Value -isnot [string] -and $propriedade.Value -isnot [ValueType]) {
                    throw "Formato não-plano em '$Caminho' (chave '$($propriedade.Name)'). Ajuste manualmente ou use 'dotnet user-secrets'."
                }
                $mapa[$propriedade.Name] = [string] $propriedade.Value
            }
        }
    }
    $mapa
}

function Write-ConfiguracaoPlana([string] $Caminho, $Mapa) {
    New-DiretorioPrivado -Caminho (Split-Path -Parent $Caminho)
    $temporario = "$Caminho.tmp"
    [System.IO.File]::WriteAllText($temporario, (([pscustomobject] $Mapa) | ConvertTo-Json -Depth 2), (New-Object System.Text.UTF8Encoding $false))
    Set-AcessoSomenteUsuario -Caminho $temporario
    Move-Item -LiteralPath $temporario -Destination $Caminho -Force
}

function Get-ValorExistente([string] $Chave) {
    # Valor já configurado no destino (para preservar as chaves criptográficas).
    if ($Destino -eq 'UserSecrets') {
        $valores = @($ProjetosUserSecrets.Values | ForEach-Object {
                $mapa = Read-ConfiguracaoPlana (Get-CaminhoUserSecrets $_)
                if ($mapa.Contains($Chave)) { $mapa[$Chave] }
            } | Where-Object { $_ } | Select-Object -Unique)
        if ($valores.Count -gt 1) { throw "Valores DIFERENTES de '$Chave' entre Web e Worker. Corrija antes de continuar." }
        if ($valores.Count -eq 1) { return $valores[0] }
        return $null
    }
    [Environment]::GetEnvironmentVariable($Chave.Replace(':', '__'), 'User')
}

function Resolve-SenhaContaExistente([securestring] $Informada, [string] $Chave, [string] $Conta, [switch] $DeConnectionString) {
    # Ordem: parâmetro > configuração já gravada (reexecução) > console. Um único prompt por vez.
    if ($Informada) {
        Write-Ok "senha de '$Conta' recebida por parâmetro"
        return (New-Object System.Management.Automation.PSCredential 'x', $Informada).GetNetworkCredential().Password
    }
    $existente = Get-ValorExistente $Chave
    if ($existente -and $DeConnectionString) {
        $construtor = New-Object System.Data.Common.DbConnectionStringBuilder
        $construtor.set_ConnectionString([string] $existente) # ".ConnectionString =" gravaria uma CHAVE do dicionário
        $existente = if ($construtor.ContainsKey('password')) { [string] $construtor['password'] } else { $null }
    }
    if ($existente) {
        Write-Ok "senha de '$Conta' reaproveitada da configuração existente"
        return $existente
    }
    Read-Senha "Senha atual da conta '$Conta'"
}

# ------------------------------------------------------------------------------------------------ 1. credenciais
Write-Etapa 'Credenciais'
if ($PularCriacaoBanco) {
    $senhaCatalogo = Resolve-SenhaContaExistente $SenhaContaCatalogo 'ConnectionStrings:Catalogo' $UsuarioCatalogo -DeConnectionString
    $senhaRuntime = Resolve-SenhaContaExistente $SenhaContaRuntime "Segredos:$RefRuntime" $UsuarioRuntimeTenant
    $senhaAdmin = Resolve-SenhaContaExistente $SenhaContaAdmin "Segredos:$RefAdmin" $UsuarioAdminTenant
    # Configuração anterior à conta migradora: não há senha a reaproveitar e a conta não existe no MySQL.
    if ($SenhaContaMigracao -or (Get-ValorExistente 'ConnectionStrings:CatalogoMigracao')) {
        $senhaMigracao = Resolve-SenhaContaExistente $SenhaContaMigracao 'ConnectionStrings:CatalogoMigracao' $UsuarioMigracaoCatalogo -DeConnectionString
    }
    else {
        $senhaMigracao = $null
        Write-Aviso "conta migradora ainda não criada: rode sem -PularCriacaoBanco para que a Web recrie o catálogo apagado (Development)"
    }
}
else {
    if ($CredencialAdministrativaMySql -eq [System.Management.Automation.PSCredential]::Empty) {
        Assert-SessaoInterativa 'a conta administrativa do MySQL (ou informe -CredencialAdministrativaMySql)'
        $informada = Get-Credential -UserName 'root' -Message 'Conta ADMINISTRATIVA do MySQL (usada só nesta execução; nunca gravada)'
        if ($null -eq $informada) { throw 'Credencial administrativa do MySQL não informada (janela cancelada). Nada foi alterado.' }
        $CredencialAdministrativaMySql = $informada
    }
    $senhaCatalogo = New-SenhaForte
    $senhaRuntime = New-SenhaForte
    $senhaAdmin = New-SenhaForte
    $senhaMigracao = New-SenhaForte
    Write-Ok 'senhas fortes geradas para catálogo, runtime, admin e migrador (não exibidas)'
}

# ------------------------------------------------------------------------------------------------ 2. MySQL
if (-not $PularCriacaoBanco) {
    $script:ClienteMySql = Find-ClienteMySql
    $adminUsuario = $CredencialAdministrativaMySql.UserName
    if ($adminUsuario -notmatch '^[A-Za-z0-9_.@\-]{1,32}$') {
        throw 'Usuário administrativo do MySQL inválido (permitidos: letras, dígitos, _ . @ -; até 32).'
    }
    $adminSenha = $CredencialAdministrativaMySql.GetNetworkCredential().Password

    Write-Etapa "MySQL $ServidorMySql`:$PortaMySql (SslMode=$SslMode)"
    $diagnostico = Invoke-MySql $adminUsuario $adminSenha "SELECT 'versao', VERSION(), @@GLOBAL.partial_revokes; SHOW SESSION STATUS LIKE 'Ssl_cipher';"
    $versao = @(($diagnostico | Where-Object { $_ -like "versao`t*" } | Select-Object -First 1) -split "`t")
    $cifra = @(($diagnostico | Where-Object { $_ -like "Ssl_cipher*" } | Select-Object -First 1) -split "`t")
    if ($versao.Count -lt 3) { throw "Resposta inesperada do MySQL: $($diagnostico -join ' | ')" }
    $tls = if ($cifra.Count -ge 2 -and $cifra[1]) { "com TLS ($($cifra[1]))" } else { 'SEM TLS' }
    Write-Ok "servidor $($versao[1]); conexão administrativa $tls"
    if ($versao[2] -eq '1') {
        throw 'partial_revokes=ON: o privilégio por padrão `tenant_%` não funcionaria. Conceda à conta admin privilégios por base ou desative partial_revokes.'
    }

    $exigencia = if ($SslMode -eq 'Disabled') { 'REQUIRE NONE' } else { 'REQUIRE SSL' }
    # Padrão `tenant_%` SEM escapar o '_': no MySQL 8.4 o GRANT OPTION concedido via `tenant\_%` permite CREATE
    # DATABASE e DDL, mas o servidor NEGA o "GRANT ... ON tenant_<id>.* TO runtime" do provisionamento (ERRO 1044).
    $sql = @"
CREATE DATABASE IF NOT EXISTS ``$BaseCatalogo`` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;
CREATE USER IF NOT EXISTS '$UsuarioCatalogo'@'$HostContas' IDENTIFIED BY '$senhaCatalogo';
ALTER USER '$UsuarioCatalogo'@'$HostContas' IDENTIFIED BY '$senhaCatalogo' $exigencia;
GRANT SELECT, INSERT, UPDATE, DELETE ON ``$BaseCatalogo``.* TO '$UsuarioCatalogo'@'$HostContas';
CREATE USER IF NOT EXISTS '$UsuarioRuntimeTenant'@'%' IDENTIFIED BY '$senhaRuntime';
ALTER USER '$UsuarioRuntimeTenant'@'%' IDENTIFIED BY '$senhaRuntime' $exigencia;
CREATE USER IF NOT EXISTS '$UsuarioAdminTenant'@'$HostContas' IDENTIFIED BY '$senhaAdmin';
ALTER USER '$UsuarioAdminTenant'@'$HostContas' IDENTIFIED BY '$senhaAdmin' $exigencia;
GRANT CREATE, ALTER, DROP, INDEX, REFERENCES, SELECT, INSERT, UPDATE, DELETE, CREATE VIEW, SHOW VIEW, LOCK TABLES, EVENT, TRIGGER ON ``portalbase_tenant_%``.* TO '$UsuarioAdminTenant'@'$HostContas' WITH GRANT OPTION;
GRANT CREATE, ALTER, DROP, INDEX, REFERENCES, SELECT, INSERT, UPDATE, DELETE, CREATE VIEW, SHOW VIEW, LOCK TABLES, EVENT, TRIGGER ON ``tenant_%``.* TO '$UsuarioAdminTenant'@'$HostContas' WITH GRANT OPTION;
GRANT SELECT, SHOW VIEW, LOCK TABLES, EVENT, TRIGGER ON ``$BaseCatalogo``.* TO '$UsuarioAdminTenant'@'$HostContas';
GRANT CREATE, ALTER, DROP, INDEX, REFERENCES, SELECT, INSERT, LOCK TABLES, CREATE VIEW, EVENT, TRIGGER ON ``$($BaseCatalogo)\_tst%``.* TO '$UsuarioAdminTenant'@'$HostContas';
CREATE USER IF NOT EXISTS '$UsuarioMigracaoCatalogo'@'$HostContas' IDENTIFIED BY '$senhaMigracao';
ALTER USER '$UsuarioMigracaoCatalogo'@'$HostContas' IDENTIFIED BY '$senhaMigracao' $exigencia;
GRANT ALL PRIVILEGES ON ``$BaseCatalogo``.* TO '$UsuarioMigracaoCatalogo'@'$HostContas';
"@
    Invoke-MySql $adminUsuario $adminSenha $sql | Out-Null
    Write-Ok "base '$BaseCatalogo' e contas '$UsuarioCatalogo', '$UsuarioRuntimeTenant', '$UsuarioAdminTenant', '$UsuarioMigracaoCatalogo' criadas/atualizadas ($exigencia)"

    if ($AplicarMigracoesCatalogo) {
        Write-Etapa 'Migrations do catálogo (dotnet ef)'
        $infra = Join-Path $RaizRepositorio 'src/PortalBase.Infrastructure'
        # Com a conta MIGRADORA (só a base de catálogo): prova que ela basta para as migrations - o mesmo que a Web usa
        # em Development para recriar o catálogo apagado. Só neste processo.
        $env:PORTALBASE_DESIGNTIME_CONNECTION = New-ConnectionString $BaseCatalogo $UsuarioMigracaoCatalogo $senhaMigracao
        Push-Location -LiteralPath $RaizRepositorio # o dotnet-ef é ferramenta LOCAL fixada em .config/dotnet-tools.json
        try {
            $codigo = Invoke-Dotnet @('tool', 'restore')
            if ($codigo -ne 0) { throw "dotnet tool restore falhou (código $codigo)." }
            $codigo = Invoke-Dotnet @('ef', 'database', 'update', '--context', 'PortalBaseCatalogoDbContext', '--project', $infra, '--startup-project', $infra)
            if ($codigo -ne 0) { throw "dotnet ef database update falhou (código $codigo)." }
        }
        finally {
            Pop-Location
            Remove-Item Env:PORTALBASE_DESIGNTIME_CONNECTION -ErrorAction SilentlyContinue
        }
        Write-Ok 'catálogo migrado'
    }

    Write-Etapa 'Verificação de menor privilégio'
    $consultaCatalogo = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '$BaseCatalogo';"
    $verificacoes = @(
        @{ Conta = $UsuarioCatalogo; Senha = $senhaCatalogo; Esperado = '1'; Descricao = 'catálogo ENXERGA a base de catálogo' },
        @{ Conta = $UsuarioRuntimeTenant; Senha = $senhaRuntime; Esperado = '0'; Descricao = 'runtime NÃO enxerga o catálogo' },
        @{ Conta = $UsuarioAdminTenant; Senha = $senhaAdmin; Esperado = '0'; Descricao = 'admin dos tenants NÃO enxerga o catálogo' },
        @{ Conta = $UsuarioMigracaoCatalogo; Senha = $senhaMigracao; Esperado = '1'; Descricao = 'migrador ENXERGA a base de catálogo' }
    )
    foreach ($verificacao in $verificacoes) {
        $obtido = (Invoke-MySql $verificacao.Conta $verificacao.Senha $consultaCatalogo | Select-Object -Last 1).Trim()
        if ($obtido -ne $verificacao.Esperado) { throw "Falha: $($verificacao.Descricao) (obtido $obtido)." }
        Write-Ok "$($verificacao.Descricao) - login com a nova senha OK"
    }

    # O migrador é restrito à base de catálogo: não cria nenhuma outra base.
    $baseAlheia = 'portalbase_verificacao_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $criouAlheia = $true
    try { Invoke-MySql $UsuarioMigracaoCatalogo $senhaMigracao "CREATE DATABASE ``$baseAlheia``;" | Out-Null } catch { $criouAlheia = $false }
    if ($criouAlheia) {
        Invoke-MySql $adminUsuario $adminSenha "DROP DATABASE IF EXISTS ``$baseAlheia``;" | Out-Null
        throw 'Falha: o migrador conseguiu criar uma base fora do catálogo (privilégio amplo demais).'
    }
    Write-Ok 'migrador NÃO cria bases fora do catálogo'

    # Cadeia do PROVISIONAMENTO com as contas reais: admin cria a base e concede DML ao runtime (mesmo GRANT do
    # AdministradorBancoMySql); runtime passa a enxergá-la. Base temporária, revogada e removida ao final.
    $baseTeste = 'portalbase_tenant_verificacao_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $concessao = "SELECT, INSERT, UPDATE, DELETE ON ``$baseTeste``.* TO '$UsuarioRuntimeTenant'@'%'"
    try {
        Invoke-MySql $UsuarioAdminTenant $senhaAdmin "CREATE DATABASE ``$baseTeste``; GRANT $concessao;" | Out-Null
        $visivel = (Invoke-MySql $UsuarioRuntimeTenant $senhaRuntime "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '$baseTeste';" | Select-Object -Last 1).Trim()
        if ($visivel -ne '1') { throw 'Falha: o runtime não recebeu acesso à base criada pelo admin.' }
        Write-Ok 'provisionamento: admin cria base portalbase_tenant_* e concede DML ao runtime'
    }
    finally {
        # Limpeza tolerante: se o GRANT falhou, o REVOKE falha também - e não pode esconder o erro original.
        try { Invoke-MySql $UsuarioAdminTenant $senhaAdmin "REVOKE $($concessao.Replace(' TO ', ' FROM '));" | Out-Null } catch { }
        Invoke-MySql $UsuarioAdminTenant $senhaAdmin "DROP DATABASE IF EXISTS ``$baseTeste``;" | Out-Null
    }
    Remove-Variable adminSenha
}

# ------------------------------------------------------------------------------------------------ 3. chaves
Write-Etapa 'Chaves criptográficas (MFA, hash de identidade, integridade da auditoria)'
$valoresChaves = [ordered]@{}
foreach ($ref in $RefsChaves.Values) {
    $chave = "Segredos:$ref"
    $existente = if ($RotacionarChavesCriptograficas) { $null } else { Get-ValorExistente $chave }
    if ($existente) {
        $valoresChaves[$chave] = $existente
        Write-Ok "$chave preservada ($($existente.Length) caracteres)"
    }
    else {
        $valoresChaves[$chave] = New-ChaveBase64
        if ($RotacionarChavesCriptograficas) { Write-Aviso "$chave ROTACIONADA - dados protegidos pela chave anterior ficam ilegíveis" }
        else { Write-Ok "$chave gerada ($($valoresChaves[$chave].Length) caracteres)" }
    }
}

# ------------------------------------------------------------------------------------------------ 4. diretórios
Write-Etapa "Diretórios privados em $DiretorioDados"
$dirChaves = Join-Path $DiretorioDados 'dataprotection-chaves'
$dirArmazenamento = Join-Path $DiretorioDados 'armazenamento'
$dirTemporario = Join-Path $DiretorioDados 'armazenamento-temp'
$dirEmails = Join-Path $DiretorioDados 'emails'
foreach ($dir in @($DiretorioDados, $dirChaves, $dirArmazenamento, $dirTemporario, $dirEmails)) { New-DiretorioPrivado $dir }
Write-Ok 'DataProtection, armazenamento, temporário e e-mails com acesso restrito ao usuário atual'

# ------------------------------------------------------------------------------------------------ 4b. e-mail
Write-Etapa 'E-mail de disparo (Esqueci a senha)'
$modoExistente = Get-ValorExistente 'Email:Modo'
$email = [ordered]@{}
# Ajustes pontuais que NÃO reconfiguram o e-mail (não removem as demais chaves Email:* já gravadas).
$emailAjustes = [ordered]@{}
if (-not $EmailModo -and $modoExistente) {
    Write-Ok "configuração de e-mail existente mantida (Email:Modo=$modoExistente); para mudar, use -EmailModo"
    # A pasta local acompanha -DiretorioDados, como as demais pastas; o endereço do portal só muda se for informado.
    if ($modoExistente -eq 'PastaLocal') { $emailAjustes['Email:PastaLocal:Caminho'] = $dirEmails }
    if ($PSBoundParameters.ContainsKey('UrlPublicaPortal')) { $emailAjustes['Email:UrlPublicaPortal'] = $UrlPublicaPortal.TrimEnd('/') }
}
else {
    $modo = if ($EmailModo) { $EmailModo } else { 'PastaLocal' }
    $email['Email:Modo'] = $modo
    $email['Email:UrlPublicaPortal'] = $UrlPublicaPortal.TrimEnd('/')
    $email['Email:Remetente:Nome'] = $EmailRemetenteNome
    switch ($modo) {
        'PastaLocal' {
            $email['Email:Remetente:Email'] = $(if ($EmailRemetente) { $EmailRemetente } else { 'nao-responda@portalbase.local' })
            $email['Email:PastaLocal:Caminho'] = $dirEmails
            Write-Ok "e-mails gravados como arquivos .eml em $dirEmails (sem servidor; abra no Outlook ou num editor)"
        }
        'Smtp' {
            $email['Email:Remetente:Email'] = $EmailRemetente
            $email['Email:Smtp:Servidor'] = $SmtpServidor
            $email['Email:Smtp:Porta'] = [string] $SmtpPorta
            $email['Email:Smtp:Seguranca'] = $SmtpSeguranca
            $email['Email:Smtp:Usuario'] = $(if ($SmtpUsuario) { $SmtpUsuario } else { '' })
            $email['Email:Smtp:SenhaRef'] = $RefSenhaSmtp
            if ($SmtpUsuario) {
                $senhaSmtpTexto = if ($SenhaSmtp) {
                    Write-Ok 'senha do SMTP recebida por parâmetro'
                    (New-Object System.Management.Automation.PSCredential 'x', $SenhaSmtp).GetNetworkCredential().Password
                }
                elseif (Get-ValorExistente "Segredos:$RefSenhaSmtp") {
                    Write-Ok 'senha do SMTP reaproveitada da configuração existente'
                    Get-ValorExistente "Segredos:$RefSenhaSmtp"
                }
                else { Read-Senha "Senha do SMTP para '$SmtpUsuario'" }
                $email["Segredos:$RefSenhaSmtp"] = $senhaSmtpTexto
            }
            if ($SmtpSeguranca -eq 'Nenhuma') { Write-Aviso 'SMTP sem TLS: aceito só em Development (servidor local de teste)' }
            Write-Ok "SMTP $SmtpServidor`:$SmtpPorta ($SmtpSeguranca), remetente $EmailRemetente$(if ($SmtpUsuario) { ", usuário $SmtpUsuario (senha não exibida)" })"
        }
        'Desligado' { Write-Aviso 'e-mail desligado: o "Esqueci a senha" fica indisponível' }
    }
}

# ------------------------------------------------------------------------------------------------ 5. configuração
$comum = [ordered]@{
    'ConnectionStrings:Catalogo'          = New-ConnectionString $BaseCatalogo $UsuarioCatalogo $senhaCatalogo
    'Vault:Modo'                          = 'ConfiguracaoLocal'
    'Vault:Ambiente'                      = 'dev'
    'BancoTenant:Servidor'                = $ServidorMySql
    'BancoTenant:Porta'                   = [string] $PortaMySql
    'BancoTenant:SslMode'                 = $SslMode
    'BancoTenant:PermitirRecuperacaoChavePublica' = $(if ($SslMode -eq 'Disabled') { 'true' } else { 'false' })
    'BancoTenant:RuntimeUsuario'          = $UsuarioRuntimeTenant
    'BancoTenant:RuntimeSenhaRef'         = $RefRuntime
    'BancoTenant:AdminUsuario'            = $UsuarioAdminTenant
    'BancoTenant:AdminSenhaRef'           = $RefAdmin
    "Segredos:$RefRuntime"                = $senhaRuntime
    "Segredos:$RefAdmin"                  = $senhaAdmin
    'Armazenamento:DiretorioRaiz'         = $dirArmazenamento
    'Armazenamento:DiretorioTemporario'   = $dirTemporario
}
foreach ($par in $RefsChaves.GetEnumerator()) { $comum[$par.Key] = $par.Value }
foreach ($par in $valoresChaves.GetEnumerator()) { $comum[$par.Key] = $par.Value }
$somenteWeb = [ordered]@{ 'DataProtection:CaminhoChaves' = $dirChaves }
# Só a Web dispara e-mail (o Worker não recebe a senha do SMTP).
foreach ($par in $email.GetEnumerator()) { $somenteWeb[$par.Key] = $par.Value }
foreach ($par in $emailAjustes.GetEnumerator()) { $somenteWeb[$par.Key] = $par.Value }
if ($senhaMigracao) {
    # Só a Web recria o catálogo (Development); o Worker não recebe credencial de DDL.
    $somenteWeb['ConnectionStrings:CatalogoMigracao'] = New-ConnectionString $BaseCatalogo $UsuarioMigracaoCatalogo $senhaMigracao
}

Write-Etapa "Gravando configuração ($Destino)"
if ($Destino -eq 'UserSecrets') {
    foreach ($projeto in $ProjetosUserSecrets.GetEnumerator()) {
        $caminho = Get-CaminhoUserSecrets $projeto.Value
        $mapa = Read-ConfiguracaoPlana $caminho
        if ($email.Count -gt 0) {
            # E-mail reconfigurado: some o que era do modo anterior (inclusive a senha do SMTP, se não houver mais).
            foreach ($chave in @($mapa.Keys | Where-Object { ("$_".StartsWith('Email:') -or "$_" -eq "Segredos:$RefSenhaSmtp") -and -not $email.Contains("$_") })) { $mapa.Remove($chave) }
        }
        foreach ($par in $comum.GetEnumerator()) { $mapa[$par.Key] = $par.Value }
        if ($projeto.Key -eq 'Web') { foreach ($par in $somenteWeb.GetEnumerator()) { $mapa[$par.Key] = $par.Value } }
        Write-ConfiguracaoPlana $caminho $mapa
        Write-Ok "$($projeto.Key): $caminho ($($mapa.Count) chaves; arquivo restrito ao usuário)"
    }
}
else {
    $todas = [ordered]@{}
    foreach ($par in $comum.GetEnumerator()) { $todas[$par.Key] = $par.Value }
    foreach ($par in $somenteWeb.GetEnumerator()) { $todas[$par.Key] = $par.Value }
    if ($email.Count -gt 0) {
        $obsoletas = @([Environment]::GetEnvironmentVariables('User').Keys | Where-Object { ("$_".StartsWith('Email__') -or "$_" -eq "Segredos__$RefSenhaSmtp") -and -not $email.Contains("$_".Replace('__', ':')) })
        foreach ($nome in $obsoletas) { [Environment]::SetEnvironmentVariable("$nome", $null, 'User') }
    }
    foreach ($par in $todas.GetEnumerator()) { [Environment]::SetEnvironmentVariable($par.Key.Replace(':', '__'), $par.Value, 'User') }
    Write-Ok "$($todas.Count) variáveis de ambiente do USUÁRIO gravadas (reabra o terminal para carregá-las)"
}

# ------------------------------------------------------------------------------------------------ 6. conflitos
Write-Etapa 'Conflitos de configuração'
$conflitos = 0
$chavesGravadas = @($comum.Keys) + @($somenteWeb.Keys)
if ($Destino -eq 'UserSecrets') {
    # Variáveis de ambiente têm PRECEDÊNCIA sobre user-secrets: uma homônima esconderia o valor gravado.
    $escopos = if ($EhWindows) { @('User', 'Machine') } else { @('Process') }
    foreach ($chave in $chavesGravadas) {
        foreach ($escopo in $escopos) {
            if ([Environment]::GetEnvironmentVariable($chave.Replace(':', '__'), $escopo)) {
                Write-Aviso "variável $($chave.Replace(':', '__')) (escopo $escopo) SOBRESCREVE o user-secrets - remova-a"
                $conflitos++
            }
        }
    }
}
if ($EhWindows) {
    $legadas = @([Environment]::GetEnvironmentVariables('Machine').Keys | Where-Object { "$_" -like 'PORTALBASE_*' })
    if ($legadas.Count -gt 0) {
        Write-Aviso "variáveis de MÁQUINA com prefixo PORTALBASE_ ($($legadas -join ', ')) NÃO são lidas por este projeto e são visíveis a todos os usuários da máquina; se guardam senha, mova-as para um cofre."
        $conflitos++
    }
}
# ASPNETCORE_ENVIRONMENT diferente de Development marca a máquina como NÃO-desenvolvimento: com o DOTNET_ENVIRONMENT do
# launchSettings, o criar-acesso-local.ps1 vê ambiente ambíguo e recusa.
$ambienteNoWindows = $false
foreach ($escopo in $(if ($EhWindows) { @('User', 'Machine') } else { @() })) {
    $valor = [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $escopo)
    if (-not [string]::IsNullOrWhiteSpace($valor) -and $valor.Trim() -cne 'Development') {
        Write-Aviso "variável ASPNETCORE_ENVIRONMENT='$valor' (escopo $escopo): o criar-acesso-local.ps1 recusa rodar (ambiente ambíguo) - remova-a: [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', `$null, '$escopo') e abra outro terminal"
        $ambienteNoWindows = $true
        $conflitos++
    }
}
if (-not $ambienteNoWindows -and -not [string]::IsNullOrWhiteSpace($env:ASPNETCORE_ENVIRONMENT) -and $env:ASPNETCORE_ENVIRONMENT.Trim() -cne 'Development') {
    Write-Aviso "variável ASPNETCORE_ENVIRONMENT='$($env:ASPNETCORE_ENVIRONMENT)' nesta sessão: o criar-acesso-local.ps1 recusa rodar (ambiente ambíguo) - remova-a: Remove-Item Env:ASPNETCORE_ENVIRONMENT"
    $conflitos++
}
if ($conflitos -eq 0) { Write-Ok 'nenhum conflito encontrado' }

Write-Etapa 'Concluído'
Write-Host '    Nenhuma senha ou chave foi exibida. Próximos passos:'
if (-not $AplicarMigracoesCatalogo -and -not $PularCriacaoBanco) {
    Write-Host '      - aplicar as migrations do catálogo (reexecute com -AplicarMigracoesCatalogo ou veja o README);'
}
Write-Host '      - .\scripts\criar-acesso-local.ps1             (primeiro login: contribuinte, usuário com senha e MFA)'
Write-Host '      - dotnet run --project src/PortalBase.Web     (ASPNETCORE_ENVIRONMENT=Development pelo launchSettings)'
Write-Host '      - dotnet run --project src/PortalBase.Worker  (DOTNET_ENVIRONMENT=Development pelo launchSettings)'
$modoEmailFinal = if ($email.Contains('Email:Modo')) { $email['Email:Modo'] } else { $modoExistente }
if ($modoEmailFinal -eq 'PastaLocal') {
    Write-Host "      - e-mails do 'Esqueci a senha' ficam em $dirEmails (para enviar de verdade: -EmailModo Smtp)"
}
