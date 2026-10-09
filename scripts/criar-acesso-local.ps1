#Requires -Version 5.1
<#
.SYNOPSIS
    Cria o primeiro acesso LOCAL ao Portal Base (somente Development): contribuinte provisionado e ativo, usuário com
    senha, vínculo e papéis. MFA é OPCIONAL (-ComMfa para cadastrar, -RemoverMfa para desligar).

.DESCRIPTION
    Atalho fino para "dotnet run --project tools/PortalBase.AcessoLocal" (o comando faz todo o trabalho e todas as
    verificações). Rode DEPOIS de .\scripts\configurar-ambiente-local.ps1 -AplicarMigracoesCatalogo: o comando lê a
    mesma configuração da Web (user-secrets portalbase-web).

    Travas (fail-closed): só roda com o ambiente Development, Vault:Modo=ConfiguracaoLocal e MySQL desta máquina
    (localhost/127.0.0.1/::1). Fora disso, acessos são criados pelos fluxos administrativos autorizados e auditados.

    A senha NUNCA é parâmetro (ficaria no histórico): é digitada sem eco e confirmada.

    MFA (opcional): por padrão o usuário fica SEM MFA e o login pede só a senha. Com -ComMfa o MFA (TOTP) é cadastrado
    no próprio terminal: a chave aparece uma única vez para você registrar no app autenticador, e o comando pede o
    código de 6 dígitos para confirmar; os códigos de recuperação também aparecem uma única vez. -RemoverMfa desliga o
    MFA de um usuário que já o tem. Sem MFA, a política do servidor nega só as operações de alto impacto (as listadas
    em Permissoes.ExigemMfa; no template, excluir dados).

    Idempotente: rodar de novo reaproveita o que já existe, completa o que faltar e não troca a senha sem
    -RedefinirSenha. Toda alteração é auditada (ator acesso-local:<seu usuário do Windows>).

    Códigos de saída: 0 sucesso, 1 falha, 2 entrada inválida, 3 recusado pela trava de ambiente.

.PARAMETER Tenant
    Nome do tenant, escolhido por você (ex.: STAKE). É o que aparece na seleção de contribuinte (sempre em
    maiúsculas) e define a base: STAKE -> portalbase_tenant_stake. Letras sem acento, dígitos e _ (2 a 40),
    começando por letra. Exigido só ao criar o contribuinte; depois é fixo.

.PARAMETER Email
    E-mail do usuário: é com ele que se entra no portal (sem diferenciar maiúsculas de minúsculas). Gravado ao criar
    o usuário e único: não pode ser o de outro usuário.

.PARAMETER Login
    Nome de usuário interno: identifica o usuário neste comando (rodar de novo com o mesmo -Login completa o acesso).

.PARAMETER Papel
    Leitor, Analista ou Administrador (um ou mais). Padrão: Administrador.

.PARAMETER SenhaStdin
    Automação: lê a senha da pipeline (não combina com -ComMfa, pois o cadastro do MFA é interativo). O atalho a
    repassa ao comando em Base64 (UTF-8), imune à página de código do console.
    Exemplo: $senha | .\scripts\criar-acesso-local.ps1 ... -SenhaStdin
    (a senha nunca é aceita como parâmetro nomeado).

.EXAMPLE
    .\scripts\criar-acesso-local.ps1 -Cnpj 11.222.333/0001-81 -Tenant STAKE -RazaoSocial 'Stake Ltda' -Login admin.local -Nome 'Admin Local' -Email admin.local@example.com

.EXAMPLE
    .\scripts\criar-acesso-local.ps1 -Cnpj 11222333000181 -Login admin.local -RedefinirSenha

.EXAMPLE
    .\scripts\criar-acesso-local.ps1 -Cnpj 11222333000181 -Login admin.local -RemoverMfa
#>
[CmdletBinding(PositionalBinding = $false)] # argumento solto vira erro, nunca cai em outro parâmetro
param(
    [string] $Cnpj,

    [string] $Tenant,

    [string] $RazaoSocial,

    [string] $Login,

    [string] $Nome,

    [string] $Email,

    [ValidateSet('Leitor', 'Analista', 'Administrador')]
    [string[]] $Papel = @('Administrador'),

    [switch] $RedefinirSenha,

    [switch] $ComMfa,

    [switch] $RemoverMfa,

    # O padrão (sem MFA); mantido por compatibilidade.
    [switch] $SemMfa,

    [switch] $SenhaStdin,

    [switch] $Ajuda,

    # Só pela PIPELINE, junto com -SenhaStdin. Passada por nome, ficaria no histórico: é recusada.
    [Parameter(ValueFromPipeline = $true, DontShow = $true)]
    [string] $EntradaSenha
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent $PSScriptRoot
$projeto = Join-Path (Join-Path (Join-Path $raiz 'tools') 'PortalBase.AcessoLocal') 'PortalBase.AcessoLocal.csproj'
if (-not (Test-Path -LiteralPath $projeto)) { throw "Projeto do comando não encontrado: $projeto" }
if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
    throw 'dotnet não encontrado no PATH. Instale o SDK .NET 10 (global.json).'
}

$senhaPelaPipeline = $PSBoundParameters.ContainsKey('EntradaSenha')
if ($senhaPelaPipeline -and -not $MyInvocation.ExpectingInput) {
    throw 'A senha não é aceita como parâmetro (ficaria no histórico). Omita-a para digitá-la sem eco, ou use a pipeline com -SenhaStdin.'
}
if ($senhaPelaPipeline -ne [bool] $SenhaStdin) {
    throw 'Com -SenhaStdin, envie a senha pela pipeline (e só então): $senha | .\scripts\criar-acesso-local.ps1 ... -SenhaStdin'
}

# A 1ª trava do comando, adiantada só para ORIENTAR (a decisão continua no comando): o launchSettings dele define
# DOTNET_ENVIRONMENT=Development, então uma ASPNETCORE_ENVIRONMENT diferente herdada desta sessão deixa o ambiente
# ambíguo e o comando recusa. Aqui se diz de onde a variável vem e como removê-la; este script não a altera.
$aspnetCore = $env:ASPNETCORE_ENVIRONMENT
if (-not $Ajuda -and -not [string]::IsNullOrWhiteSpace($aspnetCore) -and $aspnetCore.Trim() -cne 'Development') {
    $escopos = @(foreach ($escopo in @('User', 'Machine')) {
            if ([Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $escopo)) { $escopo }
        })
    $origem = if ($escopos.Count -eq 0) { 'definida só nesta sessão' }
    else { 'vem das variáveis de ambiente do Windows: ' + (($escopos | ForEach-Object { if ($_ -eq 'User') { 'usuário' } else { 'máquina' } }) -join ' e ') }
    Write-Host "[erro] Recusado: ASPNETCORE_ENVIRONMENT='$aspnetCore' ($origem). O comando só roda em Development e recusa ambiente ambíguo." -ForegroundColor Red
    Write-Host '       Para rodar agora, só nesta janela:  Remove-Item Env:ASPNETCORE_ENVIRONMENT   e rode o comando de novo.'
    if ($escopos -contains 'User') {
        Write-Host "       Para remover de vez (usuário):     [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', `$null, 'User')   e abra outro terminal."
    }
    if ($escopos -contains 'Machine') {
        Write-Host '       Para remover de vez (máquina), num PowerShell como administrador - confira antes se outra aplicação desta máquina usa a variável:'
        Write-Host "         [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', `$null, 'Machine')   e abra outro terminal."
    }
    exit 3
}

# Repassa só o que foi informado; o que faltar o comando pergunta no terminal.
$argumentos = New-Object System.Collections.Generic.List[string]
foreach ($par in @(@('--cnpj', $Cnpj), @('--tenant', $Tenant), @('--razao-social', $RazaoSocial), @('--login', $Login), @('--nome', $Nome), @('--email', $Email))) {
    if (-not [string]::IsNullOrWhiteSpace($par[1])) { $argumentos.Add($par[0]); $argumentos.Add($par[1].Trim()) }
}
foreach ($p in @($Papel | Select-Object -Unique)) { $argumentos.Add('--papel'); $argumentos.Add($p) }
if ($RedefinirSenha) { $argumentos.Add('--redefinir-senha') }
if ($ComMfa) { $argumentos.Add('--com-mfa') }
if ($RemoverMfa) { $argumentos.Add('--remover-mfa') }
if ($SenhaStdin) { $argumentos.Add('--senha-stdin-base64') }
if ($Ajuda) { $argumentos.Add('--ajuda') }

# O ambiente (Development) vem do launchSettings do comando; este script não altera variáveis da sua sessão.
if ($SenhaStdin) {
    # A senha vai pelo stdin em Base64 dos bytes UTF-8: só ASCII passa pelo pipe, então nem o $OutputEncoding do
    # Windows PowerShell 5.1 (ASCII por padrão) nem a página de código do console alteram um caractere acentuado.
    [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($EntradaSenha)) | & dotnet run --project $projeto -- $argumentos.ToArray()
}
else {
    & dotnet run --project $projeto -- $argumentos.ToArray()
}
exit $LASTEXITCODE
