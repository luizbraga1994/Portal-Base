#Requires -Version 5.1
<#
.SYNOPSIS
    Lê a configuração do ambiente atual (configurar-ambiente-local.ps1) para a produção usar a MESMA base de dados.
    Usado pelo configurar-iis.ps1 -MigrarDoAmbienteLocal; não grava nada e não exibe valor de segredo.

.DESCRIPTION
    Devolve um objeto com o servidor, a porta, a base e os usuários do MySQL, o SslMode, a pasta dos arquivos enviados e
    os segredos com os nomes de PRODUÇÃO (portalbase-<amb>-...): senhas das contas do MySQL, as três chaves (MFA, hash e
    auditoria), a chave de backup (se houver) e o que as telas gravaram (senha do SMTP, segredos de integração).
    Fonte: o secrets.json da Web (user-secrets) ou, sem ele, as variáveis de ambiente do usuário.
#>
[CmdletBinding()]
param(
    [string] $ArquivoUserSecrets,
    [Parameter(Mandatory = $true)] [string] $AmbienteCofre
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$atual = @{}
$fonte = $null
if ($ArquivoUserSecrets -and (Test-Path -LiteralPath $ArquivoUserSecrets)) {
    foreach ($p in ([System.IO.File]::ReadAllText($ArquivoUserSecrets) | ConvertFrom-Json).PSObject.Properties) { $atual[$p.Name] = [string] $p.Value }
    $fonte = $ArquivoUserSecrets
}
else {
    $variaveis = [Environment]::GetEnvironmentVariables('User')
    foreach ($nome in $variaveis.Keys) { if ("$nome" -like '*__*') { $atual["$nome".Replace('__', ':')] = [string] $variaveis[$nome] } }
    $fonte = 'variáveis de ambiente do usuário'
}
if (-not $atual.ContainsKey('ConnectionStrings:Catalogo') -or -not $atual['ConnectionStrings:Catalogo']) {
    throw "Não achei a configuração atual (ConnectionStrings:Catalogo). Rode com o usuário do Windows que rodou o configurar-ambiente-local.ps1 ou informe -ArquivoUserSecrets."
}

$conexao = New-Object System.Data.Common.DbConnectionStringBuilder
# set_ConnectionString: no PowerShell, "$conexao.ConnectionString = ..." viraria uma CHAVE do dicionário.
$conexao.set_ConnectionString($atual['ConnectionStrings:Catalogo'])
function Parte([string[]] $nomes) {
    foreach ($n in $nomes) { if ($conexao.ContainsKey($n)) { return [string] $conexao[$n] } }
    return $null
}
function Ref([string] $chave, [string] $padrao) {
    if ($atual.ContainsKey($chave) -and $atual[$chave]) { return $atual[$chave] }
    return $padrao
}
function Segredo([string] $referencia) {
    $chave = "Segredos:$referencia"
    if ($atual.ContainsKey($chave)) { return $atual[$chave] }
    return $null
}

$amb = $AmbienteCofre
$ambOrigem = Ref 'Vault:Ambiente' 'dev'
$segredos = [ordered]@{
    "portalbase-$amb-mysql-catalogo"  = Parte @('password', 'pwd')
    "portalbase-$amb-mysql-runtime"   = Segredo (Ref 'BancoTenant:RuntimeSenhaRef' 'banco-tenant-runtime')
    "portalbase-$amb-mysql-admin"     = Segredo (Ref 'BancoTenant:AdminSenhaRef' 'banco-tenant-admin')
    "portalbase-$amb-chave-mfa"       = Segredo (Ref 'SegurancaIdentidade:ChaveProtecaoMfaRef' 'identidade-mfa')
    "portalbase-$amb-chave-hash"      = Segredo (Ref 'SegurancaIdentidade:ChaveHashRef' 'identidade-hash')
    "portalbase-$amb-chave-auditoria" = Segredo (Ref 'Auditoria:ChaveIntegridadeRef' 'auditoria-integridade')
}
$refBackup = Ref 'Backup:ChaveCriptografiaRef' ''
if ($refBackup) { $segredos["portalbase-$amb-chave-backup"] = Segredo $refBackup }
# O que as telas gravaram: mesmo nome, trocando o ambiente de origem pelo de produção.
$prefixo = "Segredos:portalbase-$ambOrigem-"
foreach ($chave in @($atual.Keys | Where-Object { "$_".StartsWith($prefixo, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object)) {
    $segredos["portalbase-$amb-" + $chave.Substring($prefixo.Length)] = $atual[$chave]
}

$servidor = Ref 'BancoTenant:Servidor' (Parte @('server', 'host', 'data source'))
$porta = Parte @('port')
[pscustomobject]@{
    Fonte             = $fonte
    Servidor          = $servidor
    Porta             = $(if ($porta) { [int] $porta } else { 3306 })
    Base              = Parte @('database')
    UsuarioCatalogo   = Parte @('user id', 'uid', 'user', 'username')
    UsuarioRuntime    = Ref 'BancoTenant:RuntimeUsuario' $null
    UsuarioAdmin      = Ref 'BancoTenant:AdminUsuario' $null
    SslMode           = Ref 'BancoTenant:SslMode' (Parte @('sslmode', 'ssl mode'))
    DiretorioArquivos = Ref 'Armazenamento:DiretorioRaiz' $null
    Segredos          = $segredos
}
