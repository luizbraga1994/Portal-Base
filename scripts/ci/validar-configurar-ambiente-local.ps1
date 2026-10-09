#Requires -Version 5.1
<#
.SYNOPSIS
    Valida scripts/configurar-ambiente-local.ps1 no Windows PowerShell 5.1 contra um MySQL REAL (uso em CI Windows).

.DESCRIPTION
    Usado por .github/workflows/script-ambiente-windows.yml. Sobe um mysqld DESCARTÁVEL (o MySQL que acompanha a imagem
    do runner) num datadir novo, executa o script como um usuário faria (processo powershell.exe novo, lendo o arquivo
    do disco) e verifica: parse no 5.1, configuração gravada e ACL, privilégios reais das três contas (inclusive recusa
    sem TLS), nenhum segredo na saída, chaves preservadas e senhas rotacionadas na reexecução, -PularCriacaoBanco,
    parâmetros inválidos e a Web lendo a configuração (/health/ready = 200). Depois executa scripts/criar-acesso-local.ps1
    (acesso local: contribuinte provisionado + usuário + papel, idempotente, senha nunca por parâmetro) e faz LOGIN HTTP
    na Web com o acesso criado.

    NÃO execute numa máquina de desenvolvimento: cria contas no MySQL da porta informada e sobrescreve os user-secrets
    portalbase-web/portalbase-worker do usuário atual.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)] [string] $DiretorioTrabalho,
    [Parameter(Mandatory = $true)] [string] $DllWeb,
    [ValidateRange(1, 65535)] [int] $PortaMySql = 3306,
    # Shell que executa o script-alvo: o harness roda sempre no 5.1; o alvo pode ser o 5.1 (padrão) ou o PowerShell 7.
    [ValidateSet('powershell', 'pwsh')] [string] $ShellAlvo = 'powershell',
    [ValidateRange(1, 65535)] [int] $PortaWeb = 5251
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # a barra de progresso do Invoke-WebRequest no 5.1 é lenta e polui o log

if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw "Esta validação precisa do Windows PowerShell 5.1 (atual: $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion))."
}

$raiz = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptAlvo = Join-Path $raiz 'scripts\configurar-ambiente-local.ps1'
$scriptAcesso = Join-Path $raiz 'scripts\criar-acesso-local.ps1'
$dirDados = Join-Path $DiretorioTrabalho 'portalbase-dados'
$falhas = New-Object System.Collections.Generic.List[string]
$saidas = New-Object System.Collections.Generic.List[string]

function Confirmar([string] $Descricao, [bool] $Condicao) {
    if ($Condicao) { Write-Host "  [PASSOU] $Descricao" -ForegroundColor Green }
    else { Write-Host "  [FALHOU] $Descricao" -ForegroundColor Red; $falhas.Add($Descricao) }
}

function Write-Cenario([string] $Texto) { Write-Host "`n### $Texto" -ForegroundColor Cyan }

function Invoke-MySqlComo([string] $Usuario, [string] $Senha, [string] $Sql, [string] $Ssl = 'REQUIRED') {
    # SQL pela entrada padrão (sem problemas de aspas na linha de comando do 5.1); senha só no ambiente do processo.
    if ($Senha) { $env:MYSQL_PWD = $Senha } else { Remove-Item Env:MYSQL_PWD -ErrorAction SilentlyContinue }
    $anterior = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $saida = @($Sql | & mysql -h 127.0.0.1 -P $PortaMySql --protocol=TCP "--ssl-mode=$Ssl" -u $Usuario -N -B 2>&1 | ForEach-Object { "$_" })
        $codigo = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $anterior
        Remove-Item Env:MYSQL_PWD -ErrorAction SilentlyContinue
    }
    New-Object psobject -Property @{ Codigo = $codigo; Saida = $saida }
}

function Invoke-ScriptAlvo([string] $Parametros, [string] $SenhaRaiz) {
    # Processo NOVO do shell-alvo lendo o .ps1 do disco - exatamente como o usuário executa.
    $prefixo = ''
    if ($SenhaRaiz) {
        $env:CI_SENHA_RAIZ_MYSQL = $SenhaRaiz
        $prefixo = '$c = New-Object System.Management.Automation.PSCredential ''root'', (ConvertTo-SecureString $env:CI_SENHA_RAIZ_MYSQL -AsPlainText -Force); Remove-Item Env:CI_SENHA_RAIZ_MYSQL; '
        $Parametros = "-CredencialAdministrativaMySql `$c $Parametros"
    }
    try { Invoke-ShellAlvo "$prefixo& '$scriptAlvo' -PortaMySql $PortaMySql -DiretorioDados '$dirDados' $Parametros" }
    finally { Remove-Item Env:CI_SENHA_RAIZ_MYSQL -ErrorAction SilentlyContinue }
}

function Invoke-CriarAcesso([string] $Parametros, [string] $Senha) {
    # Como o usuário faria em automação: "$senha | .\scripts\criar-acesso-local.ps1 ... -SenhaStdin". A senha chega ao
    # processo novo pelo AMBIENTE (removida antes de chamar o script), nunca pela linha de comando.
    $prefixo = ''
    if ($Senha) {
        $env:CI_SENHA_ACESSO = $Senha
        $prefixo = '$s = $env:CI_SENHA_ACESSO; Remove-Item Env:CI_SENHA_ACESSO; $s | '
    }
    try { Invoke-ShellAlvo "$prefixo& '$scriptAcesso' $Parametros; exit `$LASTEXITCODE" }
    finally { Remove-Item Env:CI_SENHA_ACESSO -ErrorAction SilentlyContinue }
}

function Invoke-ShellAlvo([string] $Comando) {
    $codificado = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($Comando))
    $anterior = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $saida = @(& "$ShellAlvo.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $codificado 2>&1 | ForEach-Object { "$_" })
        $codigo = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $anterior
    }
    $texto = $saida -join "`n"
    $saidas.Add($texto)
    Write-Host ($saida | ForEach-Object { "    | $_" } | Out-String)
    New-Object psobject -Property @{ Codigo = $codigo; Texto = $texto }
}

function Read-Configuracao([string] $Projeto) {
    $caminho = Join-Path $env:APPDATA "Microsoft\UserSecrets\$Projeto\secrets.json"
    if (-not (Test-Path -LiteralPath $caminho)) { return $null }
    $mapa = @{}
    foreach ($p in ([System.IO.File]::ReadAllText($caminho) | ConvertFrom-Json).PSObject.Properties) { $mapa[$p.Name] = [string] $p.Value }
    $mapa
}

function Get-SenhaDaConexao([string] $Conexao) {
    $construtor = New-Object System.Data.Common.DbConnectionStringBuilder
    $construtor.set_ConnectionString($Conexao)
    [string] $construtor['password']
}

function Test-SomenteUsuarioESistema([string] $Caminho) {
    $acl = Get-Acl -LiteralPath $Caminho
    $sids = @($acl.Access | ForEach-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } | Sort-Object -Unique)
    $esperados = @([System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18') | Sort-Object -Unique
    $herdadas = @($acl.Access | Where-Object { $_.IsInherited })
    $acl.AreAccessRulesProtected -and $herdadas.Count -eq 0 -and -not (Compare-Object $sids $esperados)
}

function Invoke-ComWebNoAr([scriptblock] $Acao) {
    # Sobe a Web (Development) com a configuração gravada, espera /health/live e executa $Acao; sempre derruba a Web.
    $logSaida = Join-Path $DiretorioTrabalho 'web.out.log'
    $logErro = Join-Path $DiretorioTrabalho 'web.err.log'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$PortaWeb"
    $web = Start-Process -FilePath 'dotnet' -ArgumentList @("`"$DllWeb`"") -WorkingDirectory (Split-Path -Parent $DllWeb) `
        -PassThru -NoNewWindow -RedirectStandardOutput $logSaida -RedirectStandardError $logErro
    try {
        for ($i = 0; $i -lt 60; $i++) {
            Start-Sleep -Seconds 2
            try { [void] (Invoke-WebRequest -Uri "http://127.0.0.1:$PortaWeb/health/live" -UseBasicParsing -TimeoutSec 5); break } catch { }
            if ($web.HasExited) { break }
        }
        $resultado = & $Acao
    }
    finally {
        if (-not $web.HasExited) { Stop-Process -Id $web.Id -Force }
        $web.WaitForExit(15000) | Out-Null
        Remove-Item Env:ASPNETCORE_ENVIRONMENT, Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
    }
    $saidas.Add(([System.IO.File]::ReadAllText($logSaida) + [System.IO.File]::ReadAllText($logErro)))
    if ($resultado -ne 200 -and $resultado -ne $true) { Write-Host (Get-Content -LiteralPath $logSaida, $logErro -Tail 40 | Out-String) }
    $resultado
}

function Test-WebPronta {
    Invoke-ComWebNoAr {
        try { [int] (Invoke-WebRequest -Uri "http://127.0.0.1:$PortaWeb/health/ready" -UseBasicParsing -TimeoutSec 15).StatusCode }
        catch {
            $resposta = $_.Exception.Response
            if ($null -ne $resposta) { [int] $resposta.StatusCode } else { -2 }
        }
    }
}

function Invoke-FormularioWeb([string] $Caminho, [hashtable] $Campos, $Sessao) {
    # POST de formulário sem seguir o redirecionamento (o 302 e o Location são o que se verifica). Corpo montado à mão em
    # UTF-8 percent-encoded: não depende da codificação padrão do Invoke-WebRequest do 5.1.
    $corpo = (@($Campos.Keys | ForEach-Object { $_ + '=' + [uri]::EscapeDataString([string] $Campos[$_]) }) -join '&')
    $opcoes = @{
        Uri = "http://127.0.0.1:$PortaWeb$Caminho"; Method = 'Post'; Body = $corpo; ContentType = 'application/x-www-form-urlencoded'
        WebSession = $Sessao; MaximumRedirection = 0; UseBasicParsing = $true; ErrorAction = 'SilentlyContinue'
    }
    # 5.1: com MaximumRedirection 0 devolve a resposta 302 e só registra um erro (suprimido). 7+: o 302 vira exceção sem -SkipHttpErrorCheck.
    if ($PSVersionTable.PSVersion.Major -ge 7) { $opcoes['SkipHttpErrorCheck'] = $true }
    $r = Invoke-WebRequest @opcoes
    New-Object psobject -Property @{ Codigo = [int] $r.StatusCode; Destino = [string] $r.Headers['Location'] }
}

function Get-TokenAntiforgery([string] $Html) {
    [regex]::Match($Html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
}

function Test-LoginWeb([string] $Email, [string] $Senha, [string] $TenantEsperado) {
    # O fluxo do navegador: GET /conta/entrar (antiforgery + cookie), POST /conta/login com o E-MAIL e, com UM único contribuinte
    # vinculado, o servidor já entra nele (sem a tela de seleção): Dashboard com o NOME do tenant no topo. Controle
    # negativo: senha errada volta ao login com erro genérico.
    Invoke-ComWebNoAr {
        $base = "http://127.0.0.1:$PortaWeb"
        $sessao = $null
        $entrar = Invoke-WebRequest -Uri "$base/conta/entrar" -UseBasicParsing -SessionVariable sessao
        # Atenção: nomes de variável não diferenciam caixa no PowerShell ($Email é o parâmetro; não reutilizar).
        $postLogin = Invoke-FormularioWeb '/conta/login' @{ email = $Email; senha = $Senha; returnUrl = '/'; __RequestVerificationToken = (Get-TokenAntiforgery $entrar.Content) } $sessao
        Write-Host "    POST /conta/login (senha certa) -> $($postLogin.Codigo) $($postLogin.Destino)"
        if ($postLogin.Codigo -ne 302 -or $postLogin.Destino -ne '/') { return $false }

        $painel = Invoke-WebRequest -Uri "$base/" -UseBasicParsing -WebSession $sessao
        $titulo = [regex]::Match($painel.Content, '<title>([^<]*)</title>').Groups[1].Value
        $chip = [regex]::Match($painel.Content, 'class="ctx-chip always">.*?<span class="v">([^<]*)</span>', 'Singleline').Groups[1].Value.Trim()
        Write-Host "    GET / -> $([int] $painel.StatusCode) ($titulo); contribuinte no topo: $chip"
        if ([int] $painel.StatusCode -ne 200 -or -not $titulo.StartsWith('Dashboard') -or $chip -cne $TenantEsperado) { return $false }

        $outra = $null
        $entrar2 = Invoke-WebRequest -Uri "$base/conta/entrar" -UseBasicParsing -SessionVariable outra
        $errada = Invoke-FormularioWeb '/conta/login' @{ email = $Email; senha = ($Senha + 'x'); returnUrl = '/'; __RequestVerificationToken = (Get-TokenAntiforgery $entrar2.Content) } $outra
        Write-Host "    POST /conta/login (senha errada) -> $($errada.Codigo) $($errada.Destino)"
        $errada.Codigo -eq 302 -and $errada.Destino -eq '/conta/entrar?erro=1'
    }
}

# ---------------------------------------------------------------------------------------------------------------- 0
Write-Cenario '0. Parse no Windows PowerShell 5.1 (controle negativo do BOM + script real)'
$amostra = 'Write-Output "a ' + [char] 0x2014 + ' b"' # travessão dentro de string
$semBom = Join-Path $DiretorioTrabalho 'amostra-sem-bom.ps1'
$comBom = Join-Path $DiretorioTrabalho 'amostra-com-bom.ps1'
[System.IO.File]::WriteAllText($semBom, $amostra, (New-Object System.Text.UTF8Encoding $false))
[System.IO.File]::WriteAllText($comBom, $amostra, (New-Object System.Text.UTF8Encoding $true))
# O 5.1 lê .ps1 sem BOM na página ANSI DO SISTEMA; a do usuário (pt-BR/en-US) é 1252, a do runner pode não ser.
$ansi = [System.Text.Encoding]::Default
Write-Host "  página ANSI deste Windows: $($ansi.CodePage) ($($ansi.WebName))"
function Get-ErrosDeParseEm1252([byte[]] $Bytes) {
    $tokens = $null; $erros = $null
    [void] [System.Management.Automation.Language.Parser]::ParseInput([System.Text.Encoding]::GetEncoding(1252).GetString($Bytes), [ref] $tokens, [ref] $erros)
    @($erros).Count
}
Confirmar 'controle: o texto UTF-8 sem BOM lido em 1252 (Windows pt-BR) NÃO é analisável - o defeito original' ((Get-ErrosDeParseEm1252 ([System.IO.File]::ReadAllBytes($semBom))) -gt 0)
$quebrouNoCarregador = $false
try { [void] (& $semBom) } catch { $quebrouNoCarregador = $true }
Write-Host "  (informativo) o carregador do 5.1 nesta máquina $(if ($quebrouNoCarregador) { 'TAMBÉM recusa' } else { 'aceita' }) a amostra sem BOM"
$bytesScript = [System.IO.File]::ReadAllBytes($scriptAlvo)
Confirmar 'o script SEM o BOM, lido em 1252, continua analisável (defesa se um editor remover o BOM)' ((Get-ErrosDeParseEm1252 ($bytesScript[3..($bytesScript.Length - 1)])) -eq 0)
Confirmar 'controle: o mesmo conteúdo COM BOM é analisável e devolve o texto correto' ((& $comBom) -eq ('a ' + [char] 0x2014 + ' b'))
$parseOk = $true
try { [void] (Get-Command -Name $scriptAlvo).ScriptBlock } catch { $parseOk = $false; Write-Host $_ }
Confirmar 'scripts/configurar-ambiente-local.ps1 é analisável pelo 5.1 lendo do disco' $parseOk
$parseAcessoOk = $true
try { [void] (Get-Command -Name $scriptAcesso).ScriptBlock } catch { $parseAcessoOk = $false; Write-Host $_ }
$bytesAcesso = [System.IO.File]::ReadAllBytes($scriptAcesso)
Confirmar 'scripts/criar-acesso-local.ps1 é analisável pelo 5.1 (do disco e, sem o BOM, em 1252)' `
    ($parseAcessoOk -and (Get-ErrosDeParseEm1252 ($bytesAcesso[3..($bytesAcesso.Length - 1)])) -eq 0)

# ---------------------------------------------------------------------------------------------------------------- MySQL
Write-Cenario 'MySQL descartável (binários da imagem do runner)'
$mysqld = (Get-Command mysqld.exe -CommandType Application | Select-Object -First 1).Source
$datadir = Join-Path $DiretorioTrabalho 'mysql-dados'
$init = Start-Process -FilePath $mysqld -ArgumentList @('--initialize-insecure', "--datadir=`"$datadir`"") -Wait -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $DiretorioTrabalho 'mysqld-init.out.log') -RedirectStandardError (Join-Path $DiretorioTrabalho 'mysqld-init.err.log')
if ($init.ExitCode -ne 0) { Get-Content (Join-Path $DiretorioTrabalho 'mysqld-init.err.log'); throw "mysqld --initialize-insecure falhou ($($init.ExitCode))." }
$senhaRaiz = $null
$servidor = Start-Process -FilePath $mysqld -ArgumentList @("--datadir=`"$datadir`"", "--port=$PortaMySql", '--mysqlx=OFF', '--console') -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $DiretorioTrabalho 'mysqld.out.log') -RedirectStandardError (Join-Path $DiretorioTrabalho 'mysqld.err.log')
try {
    $pronto = $false
    for ($i = 0; $i -lt 60 -and -not $pronto; $i++) {
        Start-Sleep -Seconds 2
        $pronto = (Invoke-MySqlComo 'root' '' 'SELECT 1;' 'PREFERRED').Codigo -eq 0
    }
    if (-not $pronto) { Get-Content (Join-Path $DiretorioTrabalho 'mysqld.err.log') -Tail 40; throw 'mysqld não ficou pronto.' }

    # Senha da conta administrativa com ; e " (exercita o arquivo de opções do script). Mascarada no log do Actions.
    $senhaRaiz = 'Raiz;Teste"' + [guid]::NewGuid().ToString('N').Substring(0, 12) + 'Aa1!'
    Write-Host "::add-mask::$senhaRaiz"
    $r = Invoke-MySqlComo 'root' '' ("ALTER USER 'root'@'localhost' IDENTIFIED BY '" + $senhaRaiz + "';") 'PREFERRED'
    if ($r.Codigo -ne 0) { throw "ALTER USER root falhou: $($r.Saida -join ' | ')" }
    $versao = (Invoke-MySqlComo 'root' $senhaRaiz "SELECT VERSION(); SHOW SESSION STATUS LIKE 'Ssl_cipher';" 'REQUIRED').Saida -join ' '
    Write-Host "  servidor: $versao"

    # ------------------------------------------------------------------------------------------------------------ 1
    Write-Cenario '1. Primeira execução (do zero + migrations do catálogo)'
    $e1 = Invoke-ScriptAlvo '-AplicarMigracoesCatalogo' $senhaRaiz
    Confirmar 'execução 1 terminou com sucesso' ($e1.Codigo -eq 0)
    $web1 = Read-Configuracao 'portalbase-web'
    $worker1 = Read-Configuracao 'portalbase-worker'
    Confirmar 'user-secrets do Web e do Worker gravados' ($null -ne $web1 -and $null -ne $worker1)
    if ($null -eq $web1 -or $null -eq $worker1) { throw 'Sem configuração gravada; demais verificações impossíveis.' }

    $senhaCatalogo = Get-SenhaDaConexao $web1['ConnectionStrings:Catalogo']
    $senhaRuntime = $web1['Segredos:banco-tenant-runtime']
    $senhaAdmin = $web1['Segredos:banco-tenant-admin']
    $chaves = @('Segredos:identidade-mfa', 'Segredos:identidade-hash', 'Segredos:auditoria-integridade')
    foreach ($segredo in @($senhaCatalogo, $senhaRuntime, $senhaAdmin) + @($chaves | ForEach-Object { $web1[$_] })) { Write-Host "::add-mask::$segredo" }

    $exigidas = @('ConnectionStrings:Catalogo', 'BancoTenant:RuntimeSenhaRef', 'BancoTenant:AdminSenhaRef', 'Vault:Modo', 'Armazenamento:DiretorioRaiz', 'DataProtection:CaminhoChaves') + $chaves
    $ausentes = @($exigidas | Where-Object { -not ($web1.ContainsKey($_) -and $web1[$_]) })
    Confirmar "chaves exigidas presentes no Web ($($exigidas.Count))" ($ausentes.Count -eq 0)
    Confirmar 'chaves criptográficas iguais no Web e no Worker' (@($chaves | Where-Object { $web1[$_] -ne $worker1[$_] }).Count -eq 0)
    Confirmar 'Worker sem DataProtection:CaminhoChaves (só o Web)' (-not $worker1.ContainsKey('DataProtection:CaminhoChaves'))
    Confirmar 'conta migradora do catálogo só na Web (Worker sem credencial de DDL)' ($web1.ContainsKey('ConnectionStrings:CatalogoMigracao') -and -not $worker1.ContainsKey('ConnectionStrings:CatalogoMigracao'))
    Confirmar 'e-mail: pasta local (privada) na Web e nada de e-mail no Worker' `
        ($web1['Email:Modo'] -eq 'PastaLocal' -and $web1['Email:PastaLocal:Caminho'] -eq (Join-Path $dirDados 'emails') -and $web1['Email:UrlPublicaPortal'] -eq 'http://localhost:5251' `
            -and @($worker1.Keys | Where-Object { "$_".StartsWith('Email:') }).Count -eq 0)
    $senhaMigracao = Get-SenhaDaConexao $web1['ConnectionStrings:CatalogoMigracao']
    Write-Host "::add-mask::$senhaMigracao"
    $alvosAcl = @(
        (Join-Path $env:APPDATA 'Microsoft\UserSecrets\portalbase-web\secrets.json'),
        (Join-Path $env:APPDATA 'Microsoft\UserSecrets\portalbase-web'),
        (Join-Path $env:APPDATA 'Microsoft\UserSecrets\portalbase-worker\secrets.json'),
        $dirDados, (Join-Path $dirDados 'dataprotection-chaves'), (Join-Path $dirDados 'armazenamento'), (Join-Path $dirDados 'armazenamento-temp'), (Join-Path $dirDados 'emails'))
    Confirmar 'ACL NTFS: só o usuário atual e SYSTEM, sem herança (arquivos e diretórios)' (@($alvosAcl | Where-Object { -not (Test-SomenteUsuarioESistema $_) }).Count -eq 0)

    Write-Cenario 'Privilégios reais das contas'
    $baseTenant = 'portalbase_tenant_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    Confirmar 'catálogo lê a base de catálogo (migrada)' ((Invoke-MySqlComo 'portalbase_catalogo' $senhaCatalogo 'SELECT COUNT(*) FROM portalbase_catalogo.__EFMigrationsHistory;').Codigo -eq 0)
    Confirmar 'catálogo NÃO faz DDL' ((Invoke-MySqlComo 'portalbase_catalogo' $senhaCatalogo 'CREATE TABLE portalbase_catalogo.x (i int);').Codigo -ne 0)
    Confirmar 'runtime NÃO lê o catálogo' ((Invoke-MySqlComo 'portalbase_app' $senhaRuntime 'SELECT 1 FROM portalbase_catalogo.__EFMigrationsHistory LIMIT 1;').Codigo -ne 0)
    Confirmar 'admin NÃO lê o catálogo' ((Invoke-MySqlComo 'portalbase_admin' $senhaAdmin 'SELECT 1 FROM portalbase_catalogo.__EFMigrationsHistory LIMIT 1;').Codigo -ne 0)
    Confirmar 'migrador lê o catálogo (aplicou as migrations)' ((Invoke-MySqlComo 'portalbase_migrador' $senhaMigracao 'SELECT COUNT(*) FROM portalbase_catalogo.__EFMigrationsHistory;').Codigo -eq 0)
    Confirmar 'migrador NÃO cria base fora do catálogo' ((Invoke-MySqlComo 'portalbase_migrador' $senhaMigracao 'CREATE DATABASE portalbase_outra;').Codigo -ne 0)
    Confirmar 'admin cria base portalbase_tenant_<nome>' ((Invoke-MySqlComo 'portalbase_admin' $senhaAdmin ('CREATE DATABASE `' + $baseTenant + '`;')).Codigo -eq 0)
    Confirmar 'admin concede DML ao runtime (GRANT do provisionamento)' ((Invoke-MySqlComo 'portalbase_admin' $senhaAdmin ('GRANT SELECT, INSERT, UPDATE, DELETE ON `' + $baseTenant + '`.* TO ''portalbase_app''@''%'';')).Codigo -eq 0)
    Confirmar 'runtime enxerga a base do tenant' (((Invoke-MySqlComo 'portalbase_app' $senhaRuntime ("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '" + $baseTenant + "';")).Saida -join '').Trim() -eq '1')
    Confirmar 'runtime NÃO faz DDL no tenant' ((Invoke-MySqlComo 'portalbase_app' $senhaRuntime ('CREATE TABLE `' + $baseTenant + '`.x (i int);')).Codigo -ne 0)
    Confirmar 'admin NÃO cria base fora de tenant*' ((Invoke-MySqlComo 'portalbase_admin' $senhaAdmin 'CREATE DATABASE outra;').Codigo -ne 0)
    $semTls = @(
        (Invoke-MySqlComo 'portalbase_catalogo' $senhaCatalogo 'SELECT 1;' 'DISABLED').Codigo,
        (Invoke-MySqlComo 'portalbase_app' $senhaRuntime 'SELECT 1;' 'DISABLED').Codigo,
        (Invoke-MySqlComo 'portalbase_admin' $senhaAdmin 'SELECT 1;' 'DISABLED').Codigo,
        (Invoke-MySqlComo 'portalbase_migrador' $senhaMigracao 'SELECT 1;' 'DISABLED').Codigo)
    Confirmar 'as quatro contas são recusadas SEM TLS' (@($semTls | Where-Object { $_ -eq 0 }).Count -eq 0)
    [void] (Invoke-MySqlComo 'portalbase_admin' $senhaAdmin ('DROP DATABASE IF EXISTS `' + $baseTenant + '`;'))

    Write-Cenario 'Web lendo a configuração gravada pelo 5.1'
    Confirmar 'Web em Development: /health/ready = 200' ((Test-WebPronta) -eq 200)

    # ------------------------------------------------------------------------------------------------------------ 2
    Write-Cenario '2. Reexecução (chaves preservadas, senhas rotacionadas)'
    $e2 = Invoke-ScriptAlvo '' $senhaRaiz
    Confirmar 'execução 2 terminou com sucesso' ($e2.Codigo -eq 0)
    $web2 = Read-Configuracao 'portalbase-web'
    Confirmar 'chaves criptográficas preservadas' (@($chaves | Where-Object { $web1[$_] -ne $web2[$_] }).Count -eq 0)
    Confirmar 'senhas de runtime e admin rotacionadas' ($web2['Segredos:banco-tenant-runtime'] -ne $senhaRuntime -and $web2['Segredos:banco-tenant-admin'] -ne $senhaAdmin)
    foreach ($segredo in @((Get-SenhaDaConexao $web2['ConnectionStrings:Catalogo']), $web2['Segredos:banco-tenant-runtime'], $web2['Segredos:banco-tenant-admin'], (Get-SenhaDaConexao $web2['ConnectionStrings:CatalogoMigracao']))) { Write-Host "::add-mask::$segredo" }
    Confirmar 'Web continua pronta com as senhas novas' ((Test-WebPronta) -eq 200)

    # ------------------------------------------------------------------------------------------------------------ 3
    Write-Cenario '3. -PularCriacaoBanco (reaproveita a configuração; não toca no MySQL)'
    $e3 = Invoke-ScriptAlvo '-PularCriacaoBanco' ''
    $web3 = Read-Configuracao 'portalbase-web'
    Confirmar 'execução 3 terminou com sucesso' ($e3.Codigo -eq 0)
    Confirmar 'as quatro senhas foram reaproveitadas da configuração' (([regex]::Matches($e3.Texto, 'reaproveitada')).Count -eq 4)
    Confirmar 'valores idênticos aos da execução 2' (@($web2.Keys | Where-Object { $web2[$_] -ne $web3[$_] }).Count -eq 0)

    Write-Cenario '3b. E-mail por SMTP (senha só como segredo, só na Web) e volta para a pasta local'
    $senhaSmtp = 'Smtp-CI#' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    Write-Host "::add-mask::$senhaSmtp"
    $env:CI_SENHA_SMTP = $senhaSmtp
    try { $e3b = Invoke-ScriptAlvo "-PularCriacaoBanco -EmailModo Smtp -SmtpServidor smtp.example.com -SmtpUsuario ci@example.com -EmailRemetente ci@example.com -SenhaSmtp (ConvertTo-SecureString `$env:CI_SENHA_SMTP -AsPlainText -Force)" '' }
    finally { Remove-Item Env:CI_SENHA_SMTP -ErrorAction SilentlyContinue }
    $web3b = Read-Configuracao 'portalbase-web'
    $worker3b = Read-Configuracao 'portalbase-worker'
    Confirmar 'SMTP gravado na Web com a senha só em Segredos:email-smtp-senha (Worker sem a senha)' `
        ($e3b.Codigo -eq 0 -and $web3b['Email:Modo'] -eq 'Smtp' -and $web3b['Email:Smtp:Servidor'] -eq 'smtp.example.com' -and $web3b['Email:Smtp:Porta'] -eq '587' `
            -and $web3b['Email:Smtp:Seguranca'] -eq 'StartTls' -and $web3b['Email:Smtp:SenhaRef'] -eq 'email-smtp-senha' -and $web3b['Segredos:email-smtp-senha'] -ceq $senhaSmtp `
            -and -not $worker3b.ContainsKey('Segredos:email-smtp-senha'))
    $e3c = Invoke-ScriptAlvo '-PularCriacaoBanco -EmailModo PastaLocal' ''
    $web3c = Read-Configuracao 'portalbase-web'
    Confirmar 'de volta à pasta local: chaves do SMTP e a senha do SMTP removidas' `
        ($e3c.Codigo -eq 0 -and $web3c['Email:Modo'] -eq 'PastaLocal' -and -not $web3c.ContainsKey('Segredos:email-smtp-senha') -and -not $web3c.ContainsKey('Email:Smtp:Servidor'))
    Confirmar 'recusado: -SmtpServidor sem -EmailModo Smtp' ((Invoke-ScriptAlvo '-PularCriacaoBanco -SmtpServidor smtp.example.com' '').Codigo -ne 0)
    # Configuração de e-mail mantida (sem -EmailModo) + outro -DiretorioDados: a pasta local acompanha o diretório novo,
    # e -UrlPublicaPortal informado é aplicado; depois volta ao diretório original.
    $outroDir = Join-Path $DiretorioTrabalho 'portalbase-dados-outro'
    $e3d = Invoke-ShellAlvo "& '$scriptAlvo' -PortaMySql $PortaMySql -DiretorioDados '$outroDir' -PularCriacaoBanco -UrlPublicaPortal 'http://localhost:5299/'"
    $web3d = Read-Configuracao 'portalbase-web'
    $e3e = Invoke-ScriptAlvo '-PularCriacaoBanco -UrlPublicaPortal http://localhost:5251' ''
    $web3e = Read-Configuracao 'portalbase-web'
    Confirmar 'e-mail mantido: a pasta local acompanha -DiretorioDados e -UrlPublicaPortal informado é aplicado' `
        ($e3d.Codigo -eq 0 -and $web3d['Email:Modo'] -eq 'PastaLocal' -and $web3d['Email:PastaLocal:Caminho'] -eq (Join-Path $outroDir 'emails') `
            -and $web3d['Email:UrlPublicaPortal'] -eq 'http://localhost:5299' -and $web3d['Email:Remetente:Email'] -eq $web3c['Email:Remetente:Email'] `
            -and $e3e.Codigo -eq 0 -and $web3e['Email:PastaLocal:Caminho'] -eq (Join-Path $dirDados 'emails') -and $web3e['Email:UrlPublicaPortal'] -eq 'http://localhost:5251')

    # ------------------------------------------------------------------------------------------------------------ 4
    Write-Cenario '4. Parâmetros inválidos são recusados sem gravar nada'
    $antes = [System.IO.File]::ReadAllText((Join-Path $env:APPDATA 'Microsoft\UserSecrets\portalbase-web\secrets.json'))
    foreach ($caso in @('-SslMode Disabled -PularCriacaoBanco', '-BaseCatalogo tenant_x -PularCriacaoBanco', '-BaseCatalogo portalbase_tenant_x -PularCriacaoBanco', '-UsuarioRuntimeTenant portalbase_admin -PularCriacaoBanco', 'argumento-solto -PularCriacaoBanco')) {
        Confirmar "recusado: $caso" ((Invoke-ScriptAlvo $caso '').Codigo -ne 0)
    }
    Confirmar 'configuração intacta após as recusas' ($antes -eq [System.IO.File]::ReadAllText((Join-Path $env:APPDATA 'Microsoft\UserSecrets\portalbase-web\secrets.json')))

    # ------------------------------------------------------------------------------------------------------------ 5
    Write-Cenario '5. Acesso local (scripts/criar-acesso-local.ps1) e login HTTP na Web'
    # Senha com acento: prova a cadeia UTF-8 pipeline do PowerShell -> stdin do comando -> hash -> formulário da Web.
    $senhaAcesso = 'Acesso-Local#' + [guid]::NewGuid().ToString('N').Substring(0, 10) + [char] 0x00E1
    Write-Host "::add-mask::$senhaAcesso"
    $parametrosAcesso = "-Cnpj 11.222.333/0001-81 -Login admin.ci -SemMfa"
    # Diagnóstico do lado do LOGIN: o 5.1 (.NET Framework) precisa codificar o acento em UTF-8 no formulário.
    Confirmar 'o formulário de login do harness codifica acento em UTF-8 (%C3%A1)' (([uri]::EscapeDataString([string] [char] 0x00E1)) -eq '%C3%A1')
    $a1 = Invoke-CriarAcesso "$parametrosAcesso -Tenant STAKE_CI -RazaoSocial 'Contribuinte CI Ltda' -Nome 'Admin CI' -Email admin.ci@example.com -SenhaStdin" $senhaAcesso
    Confirmar 'criar-acesso-local.ps1 criou o acesso (contribuinte provisionado e ativo, usuário, vínculo e papel)' `
        ($a1.Codigo -eq 0 -and $a1.Texto.Contains('base do contribuinte provisionada') -and $a1.Texto.Contains('Acesso local pronto'))
    $baseStake = ((Invoke-MySqlComo 'root' $senhaRaiz "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'portalbase_tenant_stake_ci';").Saida -join '').Trim()
    Confirmar 'a base do tenant tem o nome escolhido: -Tenant STAKE_CI -> portalbase_tenant_stake_ci' ($baseStake -eq '1')
    $a2 = Invoke-CriarAcesso $parametrosAcesso ''
    # Trechos verificados só em ASCII: a saída capturada passa pela página OEM do runner (437), que não tem todos os acentos.
    Confirmar 'reexecução é idempotente (senha mantida; nada criado, vinculado ou concedido de novo)' `
        ($a2.Codigo -eq 0 -and $a2.Texto.Contains('senha foi mantida') -and -not $a2.Texto.Contains('tenant criado') `
            -and -not $a2.Texto.Contains('vinculado ao contribuinte') -and -not $a2.Texto.Contains('concedidos: '))
    $a3 = Invoke-CriarAcesso "$parametrosAcesso -SenhaStdin -EntradaSenha 'Nao-Aceita#2026x'" ''
    Confirmar 'senha como parâmetro nomeado é recusada' ($a3.Codigo -ne 0 -and $a3.Texto.Contains('aceita como par'))
    $a4 = Invoke-CriarAcesso '-Cnpj 11.222.333/0001-00 -Login admin.ci -SemMfa' ''
    Confirmar 'CNPJ inválido é recusado com o código de entrada inválida (2)' ($a4.Codigo -eq 2)
    $a5 = Invoke-CriarAcesso "-Cnpj 11.444.777/0001-61 -Tenant stake_ci -RazaoSocial 'Outro CI Ltda' -Login outro.ci -Nome 'Outro CI' -Email outro.ci@example.com -SemMfa -SenhaStdin" $senhaAcesso
    Confirmar 'nome de tenant já usado por outro CNPJ é recusado (código 2), sem gravar' ($a5.Codigo -eq 2 -and $a5.Texto.Contains('nada foi gravado'))
    # Windows marcado como Production (ASPNETCORE_ENVIRONMENT herdada pela sessão): o atalho recusa ANTES do dotnet, com o
    # código da trava (3) e a correção ('Para rodar agora' só existe na mensagem do atalho), e nada é gravado.
    $a6 = Invoke-ShellAlvo "`$env:ASPNETCORE_ENVIRONMENT = 'Production'; & '$scriptAcesso' -Cnpj 11.444.777/0001-61 -Tenant AMBIENTE_CI -RazaoSocial 'Ambiente CI Ltda' -Login ambiente.ci -Nome 'Ambiente CI' -Email ambiente.ci@example.com; exit `$LASTEXITCODE"
    $baseAmbiente = ((Invoke-MySqlComo 'root' $senhaRaiz "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'portalbase_tenant_ambiente_ci';").Saida -join '').Trim()
    Confirmar 'ASPNETCORE_ENVIRONMENT=Production na sessão: recusado (código 3) com a correção, sem criar a base' `
        ($a6.Codigo -eq 3 -and $a6.Texto.Contains('Para rodar agora') -and $a6.Texto.Contains('Remove-Item Env:ASPNETCORE_ENVIRONMENT') -and $baseAmbiente -eq '0')
    Confirmar 'login HTTP na Web pelo e-mail do acesso criado (senha certa -> Dashboard direto no tenant STAKE_CI; errada -> erro genérico)' (Test-LoginWeb 'admin.ci@example.com' $senhaAcesso 'STAKE_CI')

    # ------------------------------------------------------------------------------------------------------------ 6
    Write-Cenario '6. Nenhum segredo na saída dos scripts nem nos logs da Web'
    $segredos = @($senhaRaiz, $senhaCatalogo, $senhaRuntime, $senhaAdmin, (Get-SenhaDaConexao $web2['ConnectionStrings:Catalogo']),
        $web2['Segredos:banco-tenant-runtime'], $web2['Segredos:banco-tenant-admin'], $senhaAcesso, $senhaMigracao, $senhaSmtp,
        (Get-SenhaDaConexao $web2['ConnectionStrings:CatalogoMigracao'])) + @($chaves | ForEach-Object { $web1[$_] })
    $segredos = @($segredos | Where-Object { $_ })
    $vazados = @(foreach ($texto in $saidas) { foreach ($segredo in $segredos) { if ($texto.Contains($segredo)) { $segredo } } })
    Confirmar "nenhum dos $($segredos.Count) segredos aparece em $($saidas.Count) saídas capturadas" ($vazados.Count -eq 0)
}
finally {
    # Desliga o mysqld descartável e espera o processo sair (a porta precisa estar livre para a próxima etapa).
    [void] (Invoke-MySqlComo 'root' $senhaRaiz 'SHUTDOWN;' 'PREFERRED')
    if (-not $servidor.WaitForExit(60000)) { Stop-Process -Id $servidor.Id -Force -ErrorAction SilentlyContinue; [void] $servidor.WaitForExit(15000) }
    Write-Host "  mysqld descartável encerrado: $($servidor.HasExited)"
}

Write-Host ''
if ($falhas.Count -gt 0) {
    Write-Host "FALHOU: $($falhas.Count) verificação(ões):" -ForegroundColor Red
    $falhas | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "Todas as verificações passaram (harness no Windows PowerShell 5.1; script executado por $ShellAlvo)." -ForegroundColor Green
exit 0
