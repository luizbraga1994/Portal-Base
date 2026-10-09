#Requires -Version 5.1
<#
.SYNOPSIS
    Prepara um servidor Windows com IIS para rodar o Portal Base em PRODUÇÃO com o cofre local do Windows
    (Vault:Modo=CofreWindows) - sem Azure e sem nenhuma senha na configuração.

.DESCRIPTION
    Execute como ADMINISTRADOR, no servidor, no Windows PowerShell 5.1 (powershell.exe). O script:
      1. confere o ASP.NET Core Module (Hosting Bundle do .NET 10);
      2. cria/ajusta o pool de aplicativos (Sem Código Gerenciado, identidade própria do pool);
      3. cria/ajusta o SERVIÇO DO WINDOWS do Worker: conta virtual própria, início automático (atrasado) e reinício
         automático se ele parar com erro; registra as origens "PortalBase.Web" e "PortalBase.Worker" no log
         Aplicativo do Windows (avisos e erros dos dois aparecem no Visualizador de Eventos);
      4. cria as pastas privadas (dados, temporários, chaves de sessão, backups e cofre) com ACL só para o pool, o
         Worker e os administradores;
      5. cria no cofre as chaves que faltam (MFA, hash, auditoria, backups - nunca troca uma existente) e PEDE, sem eco,
         as senhas do MySQL que ainda não estão no cofre;
      6. grava a configuração (só nomes, caminhos e referências ao cofre - nenhuma senha) em
         %ProgramData%\PortalBase\PortalBase.Web.json e PortalBase.Worker.json, lidos pelo site, pelo serviço e pelo
         .exe aberto à mão como administrador; no pool e no serviço fica só o ambiente (Production);
      7. liga o BACKUP AUTOMÁTICO: acha o mysqldump/mysql do MySQL instalado, liga o agendamento diário (catálogo,
         bases e arquivos de cada contribuinte, cifrados) e, com -DiretorioOffsite, a cópia externa conferida pelo hash;
      8. recicla o pool, reinicia o serviço e confere se ele ficou em execução (senão mostra os últimos erros).

    Pode ser executado de novo (mantém o que já existe). Antes dele: contas e banco do catálogo criados no MySQL e as
    migrations do catálogo aplicadas (docs/AMBIENTE-LOCAL.md e docs/DEPLOY.md).

.PARAMETER Dominio
    Nome(s) do site como os usuários acessam (AllowedHosts), ex.: portal.aaconsulting.com.br. Vários: separados por ';'.

.PARAMETER ServidorMySql
    Servidor do MySQL (nome ou IP). Use 'localhost' se estiver nesta máquina.

.PARAMETER SslMode
    TLS até o MySQL. Required (padrão: cifra o tráfego), VerifyCA ou VerifyFull (também confere o certificado do
    servidor - recomendado quando o MySQL está em outra máquina e tem certificado de uma CA confiável).

.PARAMETER ExecutavelWorker
    Caminho do PortalBase.Worker.exe publicado. Informado e o serviço não existindo, o serviço é criado; existindo com
    outro executável, o caminho é atualizado. Se OUTRO serviço (outro nome) já roda este executável, o script para e
    pede -ServicoWorker com o nome dele (nunca cria um segundo Worker).

.PARAMETER ServicoWorker
    Nome do serviço do Windows do Worker (padrão: PortalBase.Worker). Serviço existente rodando como LocalSystem,
    LocalService ou NetworkService passa para a conta virtual própria (NT SERVICE\<nome>); conta de usuário é mantida.

.PARAMETER AplicacaoIis
    (Opcional) Caminho da aplicação no IIS para usar o pool, ex.: 'Default Web Site/Projetos/PortalBase_Web'.

.PARAMETER CaminhoSite
    Pasta publicada da Web (onde está o PortalBase.Web.exe). Necessária com -CriarPrimeiroAdministrador. Informada, o
    pool recebe leitura nela e o script avisa se o caminho físico do site no IIS for outro.

.PARAMETER CriarPrimeiroAdministrador
    Depois de configurar, cria o primeiro Administrador da plataforma (pede nome, e-mail e senha sem eco). Só funciona
    enquanto o catálogo não tem nenhum administrador.

.PARAMETER MigrarDoAmbienteLocal
    A produção vai usar a MESMA base que o ambiente atual (configurado pelo configurar-ambiente-local.ps1). Copia para o
    cofre, com os nomes de produção, as senhas das contas do MySQL, as três chaves (MFA, hash e auditoria - precisam ser
    AS MESMAS, senão o MFA cadastrado e a integridade da auditoria quebram) e o que as telas gravaram (senha do SMTP,
    segredos de integração); usa o mesmo servidor, base e usuários do MySQL; e copia os arquivos já enviados para a
    pasta de dados nova. Nunca troca um segredo que já está no cofre (sem -Substituir). Rode com o MESMO usuário do
    Windows que rodou o configurar-ambiente-local.ps1 (o user-secrets fica no perfil dele) ou informe -ArquivoUserSecrets.

.PARAMETER AceitarUploadSemAntivirus
    O portal varre cada planilha enviada com o Microsoft Defender Antivirus do servidor (MpCmdRun.exe, automático no
    Windows) e, por padrão (Upload:AntivirusObrigatorio=true), RECUSA o envio quando o Defender não está disponível
    ("Arquivo rejeitado"). O script testa o Defender no fim. Só se ele não estiver disponível (ex.: outro antivírus
    ativo no lugar dele), esta opção grava Upload:AntivirusObrigatorio=false: aceita os arquivos SEM varredura quando
    não houver motor (o tipo real, o tamanho e o hash continuam conferidos; ameaça encontrada ou falha da varredura
    continuam recusadas). É uma decisão de risco: use só com proteção equivalente no servidor e registre a decisão.

.PARAMETER DiretorioBinariosMySql
    (Opcional) Pasta com o mysqldump.exe e o mysql.exe (ex.: 'C:\Program Files\MySQL\MySQL Server 8.4\bin'). Sem ela, o
    script procura no PATH e em Program Files\MySQL. Sem os dois programas, o backup automático NÃO é ligado (aviso no
    fim e erro "BackupAgendadoDesligado" em Operações > Saúde): instale o MySQL Server/Shell ou informe a pasta.

.PARAMETER DiretorioOffsite
    (Recomendado) Pasta da cópia EXTERNA dos backups em OUTRO disco ou servidor (ex.: 'E:\CopiaPortalBase' ou
    '\\servidor-backup\PortalBase'). Cada artefato (já cifrado) é copiado e conferido pelo SHA-256. Sem ela, os backups
    ficam só em <DiretorioBase>\backups: se o disco ou o servidor se perder, os backups se perdem junto. Num
    compartilhamento de rede, dê permissão de Modificar à CONTA DO COMPUTADOR (DOMINIO\SERVIDOR$): o pool e o Worker
    acessam a rede com ela.

.PARAMETER HorarioBackupUtc
    Horário diário do backup automático, em UTC (HH:mm). Padrão 03:00 UTC (meia-noite em Brasília).

.PARAMETER SemBackupAgendado
    Não liga o backup automático (só o backup pela tela). Use apenas se outra ferramenta faz o backup do MySQL e dos
    arquivos - em produção, sem backup automático, Operações mostra erro.

.PARAMETER DiasRetencaoLog
    Por quantos dias os arquivos de log (<DiretorioBase>\logs, um por dia para a Web e outro para o Worker) ficam
    guardados. Padrao 30.

.PARAMETER EmailsAlerta
    (Recomendado) Quem recebe os alertas do portal por e-mail, separados por ponto e vírgula (ex.:
    'ti@empresa.com.br;suporte@empresa.com.br'). A cada 5 minutos o portal confere o serviço do Worker, o backup do
    catálogo (ultimas 26 h), o espaço livre dos discos das pastas do portal e os e-mails que não
    saíram; cada problema vai para o Visualizador de Eventos (origem PortalBase.Web), para
    Auditoria > Incidentes (alto ou crítico) e, com esta opção, por e-mail (repetido a cada 24 h enquanto persistir).
    O envio usa o servidor de e-mail de Configurações > E-mail.

.PARAMETER ArquivoUserSecrets
    secrets.json da Web do ambiente atual (padrão: %APPDATA%\Microsoft\UserSecrets\portalbase-web\secrets.json). Sem o
    arquivo, lê as variáveis de ambiente do usuário (configurar-ambiente-local.ps1 -Destino VariaveisUsuario).

.EXAMPLE
    .\configurar-iis.ps1 -Dominio portal.aaconsulting.com.br -ServidorMySql localhost `
        -DiretorioBase D:\PortalBase -ExecutavelWorker C:\PortalBase\Worker\PortalBase.Worker.exe `
        -AplicacaoIis 'Default Web Site/Projetos/PortalBase_Web' `
        -CaminhoSite C:\inetpub\wwwroot\Projetos\PortalDere_Web -CriarPrimeiroAdministrador
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Dominio,
    [string] $ServidorMySql,
    [int] $PortaMySql = 3306,
    [ValidateSet('Required', 'VerifyCA', 'VerifyFull')] [string] $SslMode = 'Required',
    [string] $Pool = 'PortalBase',
    [string] $AplicacaoIis,
    [string] $DiretorioBase = 'D:\PortalBase',
    [string] $AmbienteCofre = 'prod',
    [string] $BaseCatalogo = 'portalbase_catalogo',
    [string] $UsuarioCatalogo = 'portalbase_catalogo',
    [string] $UsuarioRuntime = 'portalbase_app',
    [string] $UsuarioAdmin = 'portalbase_admin',
    [string] $ServicoWorker = 'PortalBase.Worker',
    [string] $ExecutavelWorker,
    [switch] $SemWorker,
    [string] $CaminhoSite,
    [switch] $CriarPrimeiroAdministrador,
    [switch] $MigrarDoAmbienteLocal,
    [switch] $AceitarUploadSemAntivirus,
    [string] $DiretorioBinariosMySql,
    [string] $DiretorioOffsite,
    [ValidatePattern('^([01][0-9]|2[0-3]):[0-5][0-9]$')] [string] $HorarioBackupUtc = '03:00',
    [switch] $SemBackupAgendado,
    [ValidateRange(1, 3650)] [int] $DiasRetencaoLog = 30,
    [string] $EmailsAlerta,
    [string] $ArquivoUserSecrets = (Join-Path $env:APPDATA 'Microsoft\UserSecrets\portalbase-web\secrets.json'),
    [switch] $Substituir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Etapa([string] $texto) { Write-Host ''; Write-Host "== $texto" -ForegroundColor Cyan }

# ---------------------------------------------------------------- pré-requisitos
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Execute este script como Administrador.'
}

if ($AmbienteCofre -notmatch '^[a-z0-9-]{1,20}$') { throw 'AmbienteCofre: use letras minúsculas, dígitos e hífen (ex.: prod).' }
# Disco da pasta de dados: conferido antes de qualquer mudança no servidor.
if (-not [System.IO.Path]::IsPathRooted($DiretorioBase)) { throw "DiretorioBase precisa ser um caminho completo (ex.: C:\PortalBase-Dados)." }
$raizDados = [System.IO.Path]::GetPathRoot($DiretorioBase)
if (-not (Test-Path -LiteralPath $raizDados)) {
    throw "O disco '$raizDados' não existe neste servidor. Rode de novo com, por exemplo, -DiretorioBase C:\PortalBase-Dados."
}
$cofreScript = Join-Path $PSScriptRoot 'cofre-windows.ps1'
if (-not (Test-Path -LiteralPath $cofreScript)) { throw "Não achei $cofreScript (mantenha os dois scripts na mesma pasta)." }

Etapa 'ASP.NET Core Module'
$ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
if (-not (Test-Path -LiteralPath $ancm)) {
    throw 'ASP.NET Core Module não encontrado. Instale o ".NET 10 Hosting Bundle" (dotnet.microsoft.com) e rode "iisreset".'
}
Write-Host 'OK.'

Import-Module WebAdministration

# ---------------------------------------------------------------- pool do IIS
Etapa "Pool de aplicativos '$Pool'"
if (-not (Test-Path "IIS:\AppPools\$Pool")) { New-WebAppPool -Name $Pool | Out-Null; Write-Host 'Criado.' }
Set-ItemProperty "IIS:\AppPools\$Pool" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$Pool" -Name processModel.identityType -Value 'ApplicationPoolIdentity'
Set-ItemProperty "IIS:\AppPools\$Pool" -Name processModel.loadUserProfile -Value $true
Set-ItemProperty "IIS:\AppPools\$Pool" -Name startMode -Value 'AlwaysRunning'
# Sem desligar por ociosidade: os alertas (ex.: Worker parado) rodam dentro da Web e precisam dela no ar de madrugada.
Set-ItemProperty "IIS:\AppPools\$Pool" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
if ($AplicacaoIis) {
    $caminhoIis = 'IIS:\Sites\' + ($AplicacaoIis -replace '/', '\')
    if (-not (Test-Path $caminhoIis)) { throw "Aplicação do IIS não encontrada: $AplicacaoIis" }
    Set-ItemProperty $caminhoIis -Name applicationPool -Value $Pool
    # Sobe a aplicação junto com o pool (sem esperar a primeira visita). Precisa do recurso "Inicialização de
    # Aplicativos" do IIS; sem ele, a aplicação sobe na primeira requisição, como antes.
    try { Set-ItemProperty $caminhoIis -Name preloadEnabled -Value $true -ErrorAction Stop }
    catch { Write-Warning "Não foi possível ligar o pré-carregamento da aplicação (instale o recurso 'Inicialização de Aplicativos' do IIS): $($_.Exception.Message)" }
    Write-Host "A aplicação '$AplicacaoIis' passa a usar o pool '$Pool'."
    if ($CaminhoSite) {
        $fisico = ''
        try { $fisico = [Environment]::ExpandEnvironmentVariables([string] (Get-Item $caminhoIis).physicalPath).TrimEnd('\') } catch { }
        if ($fisico -and ($fisico -ne $CaminhoSite.TrimEnd('\'))) {
            Write-Warning "O caminho físico de '$AplicacaoIis' no IIS é '$fisico', diferente de -CaminhoSite '$CaminhoSite'. Confira em Configurações Básicas."
        }
    }
}
$contaPool = "IIS AppPool\$Pool"
Write-Host "Conta do pool: $contaPool"
if ($CaminhoSite) {
    if (-not (Test-Path -LiteralPath $CaminhoSite -PathType Container)) { throw "Pasta do site não encontrada: $CaminhoSite" }
    & icacls.exe $CaminhoSite /grant "$($contaPool):(OI)(CI)RX" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "Não foi possível dar leitura em $CaminhoSite para $contaPool (icacls $LASTEXITCODE)." }
    else { Write-Host "Leitura em $CaminhoSite para $contaPool." }
}

# ---------------------------------------------------------------- serviço do Worker (serviço do Windows)
$FonteWorker = 'PortalBase.Worker'   # origem no log Aplicativo (fixa no código, independe do nome do serviço)
$FonteWeb = 'PortalBase.Web'
$contaWorker = $null
if (-not $SemWorker) {
    Etapa "Serviço do Windows '$ServicoWorker' (Worker)"
    if ($ServicoWorker -notmatch '^[A-Za-z0-9._-]{1,80}$') { throw 'ServicoWorker: use letras, dígitos, ponto, hífen e sublinhado.' }
    $contaVirtual = "NT SERVICE\$ServicoWorker"
    if ($ExecutavelWorker) {
        if (-not (Test-Path -LiteralPath $ExecutavelWorker -PathType Leaf)) { throw "Worker não encontrado: $ExecutavelWorker" }
        $ExecutavelWorker = (Resolve-Path -LiteralPath $ExecutavelWorker).Path
        # Dois Workers no mesmo servidor, não: outro serviço (criado à mão, com outro nome) já roda este executável?
        $padrao = '*' + [System.Management.Automation.WildcardPattern]::Escape($ExecutavelWorker) + '*'
        $outro = @(Get-CimInstance -ClassName Win32_Service | Where-Object { $_.Name -ne $ServicoWorker -and $_.PathName -like $padrao })
        if ($outro.Count -gt 0) {
            throw "O serviço '$($outro[0].Name)' já executa $ExecutavelWorker. Rode de novo com -ServicoWorker '$($outro[0].Name)'."
        }
    }
    foreach ($parecido in @(Get-CimInstance -ClassName Win32_Service | Where-Object { $_.Name -ne $ServicoWorker -and ($_.Name -like '*PortalBase*' -or $_.DisplayName -like '*Portal Base*') })) {
        Write-Warning "Há também o serviço '$($parecido.Name)' ($($parecido.PathName)). Se for outro Worker, pare-o: só um Worker por servidor."
    }

    $servico = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServicoWorker'"
    if (-not $servico) {
        if ($ExecutavelWorker) {
            & sc.exe create $ServicoWorker binPath= "`"$ExecutavelWorker`"" start= delayed-auto obj= $contaVirtual DisplayName= 'Portal Base Worker' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Não foi possível criar o serviço (sc.exe saiu com $LASTEXITCODE)." }
            & sc.exe description $ServicoWorker 'Portal Base - heartbeat e backup agendado.' | Out-Null
            Write-Host "Serviço criado com a conta virtual $contaVirtual."
        }
        else {
            Write-Warning "Serviço '$ServicoWorker' não existe. Rode de novo com -ExecutavelWorker <caminho do PortalBase.Worker.exe> (ou -SemWorker)."
        }
    }
    else {
        Write-Host "O serviço já existe (conta: $($servico.StartName); estado: $($servico.State))."
        if ($ExecutavelWorker -and ($servico.PathName.Trim().Trim('"') -ne $ExecutavelWorker)) {
            & sc.exe config $ServicoWorker binPath= "`"$ExecutavelWorker`"" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Não foi possível trocar o executável do serviço (sc.exe saiu com $LASTEXITCODE)." }
            Write-Host "Executável do serviço: $ExecutavelWorker (antes: $($servico.PathName))."
        }
        # Contas genéricas do Windows dão ao Worker mais acesso do que ele precisa: passa para a conta virtual própria.
        $genericas = @('LocalSystem', 'NT AUTHORITY\SYSTEM', 'NT AUTHORITY\LocalService', 'NT AUTHORITY\LOCAL SERVICE', 'NT AUTHORITY\NetworkService', 'NT AUTHORITY\NETWORK SERVICE')
        if ($genericas -contains [string] $servico.StartName) {
            & sc.exe config $ServicoWorker obj= $contaVirtual | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Não foi possível trocar a conta do serviço (sc.exe saiu com $LASTEXITCODE)." }
            Write-Host "Conta do serviço trocada de '$($servico.StartName)' para '$contaVirtual'."
        }
        & sc.exe config $ServicoWorker start= delayed-auto | Out-Null
    }

    $servico = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServicoWorker'"
    if ($servico) {
        $contaWorker = [string] $servico.StartName
        if ($contaWorker.StartsWith('.\')) { $contaWorker = "$env:COMPUTERNAME\" + $contaWorker.Substring(2) }
        # Parou com erro (ex.: MySQL fora do ar)? O Windows reinicia: 1 min, 1 min e depois a cada 5 min (zera em 1 dia).
        & sc.exe failure $ServicoWorker reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Não foi possível configurar o reinício automático (sc.exe saiu com $LASTEXITCODE)." }
        & sc.exe failureflag $ServicoWorker 1 | Out-Null
        if ($ExecutavelWorker) {
            $pastaWorker = Split-Path -Parent $ExecutavelWorker
            & icacls.exe $pastaWorker /grant "$($contaWorker):(OI)(CI)RX" /Q | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warning "Não foi possível dar leitura em $pastaWorker para $contaWorker (icacls $LASTEXITCODE)." }
        }
        Write-Host "Conta: $contaWorker; início automático (atrasado); reinício automático se parar com erro."
    }
}

# ---------------------------------------------------------------- log de eventos do Windows
Etapa 'Log de eventos do Windows (Visualizador de Eventos > Aplicativo)'
foreach ($fonte in @($FonteWeb, $FonteWorker)) {
    if ([System.Diagnostics.EventLog]::SourceExists($fonte)) { Write-Host "Origem '$fonte': já registrada." }
    else {
        [System.Diagnostics.EventLog]::CreateEventSource($fonte, 'Application')
        Write-Host "Origem '$fonte' registrada."
    }
}

# ---------------------------------------------------------------- pastas privadas
function Proteger-Pasta([string] $caminho, [string[]] $contas, [string] $direito = 'Modify') {
    New-Item -ItemType Directory -Path $caminho -Force | Out-Null
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $heranca = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagacao = [System.Security.AccessControl.PropagationFlags]::None
    foreach ($sid in @('S-1-5-32-544', 'S-1-5-18')) {
        $conta = (New-Object System.Security.Principal.SecurityIdentifier($sid)).Translate([System.Security.Principal.NTAccount])
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($conta, 'FullControl', $heranca, $propagacao, 'Allow')))
    }
    foreach ($c in $contas) {
        if ($c) { $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($c, $direito, $heranca, $propagacao, 'Allow'))) }
    }
    Set-Acl -LiteralPath $caminho -AclObject $acl
}

Etapa "Pastas privadas em $DiretorioBase"
$pastas = @{
    Dados    = Join-Path $DiretorioBase 'dados'
    Tmp      = Join-Path $DiretorioBase 'tmp'
    Chaves   = Join-Path $DiretorioBase 'chaves-sessao'
    Backups  = Join-Path $DiretorioBase 'backups'
    Cofre    = Join-Path $DiretorioBase 'cofre'
    Logs     = Join-Path $DiretorioBase 'logs'
}
$ambos = @($contaPool, $contaWorker) | Where-Object { $_ }
Proteger-Pasta $DiretorioBase @()
Proteger-Pasta $pastas.Dados $ambos
Proteger-Pasta $pastas.Tmp $ambos
Proteger-Pasta $pastas.Backups $ambos
Proteger-Pasta $pastas.Logs $ambos
Proteger-Pasta $pastas.Chaves @($contaPool)
& $cofreScript -Acao Preparar -Diretorio $pastas.Cofre -Contas $ambos
foreach ($p in $pastas.GetEnumerator()) { Write-Host ("{0,-8} {1}" -f $p.Key, $p.Value) }

# ---------------------------------------------------------------- migração do ambiente atual (opcional)
$amb = $AmbienteCofre
if ($MigrarDoAmbienteLocal) {
    Etapa 'Migração do ambiente atual (mesma base de dados)'
    $atual = & (Join-Path $PSScriptRoot 'ler-ambiente-local.ps1') -ArquivoUserSecrets $ArquivoUserSecrets -AmbienteCofre $amb
    Write-Host "Configuração atual: $($atual.Fonte)"

    # Servidor, base e usuários: os MESMOS do ambiente atual (parâmetro informado prevalece).
    if (-not $PSBoundParameters.ContainsKey('ServidorMySql') -and $atual.Servidor) { $ServidorMySql = $atual.Servidor }
    if (-not $PSBoundParameters.ContainsKey('PortaMySql')) { $PortaMySql = $atual.Porta }
    if (-not $PSBoundParameters.ContainsKey('BaseCatalogo') -and $atual.Base) { $BaseCatalogo = $atual.Base }
    if (-not $PSBoundParameters.ContainsKey('UsuarioCatalogo') -and $atual.UsuarioCatalogo) { $UsuarioCatalogo = $atual.UsuarioCatalogo }
    if (-not $PSBoundParameters.ContainsKey('UsuarioRuntime') -and $atual.UsuarioRuntime) { $UsuarioRuntime = $atual.UsuarioRuntime }
    if (-not $PSBoundParameters.ContainsKey('UsuarioAdmin') -and $atual.UsuarioAdmin) { $UsuarioAdmin = $atual.UsuarioAdmin }
    if (-not $PSBoundParameters.ContainsKey('SslMode') -and $atual.SslMode) {
        if (@('Required', 'VerifyCA', 'VerifyFull') -notcontains $atual.SslMode) {
            throw "O ambiente atual usa SslMode=$($atual.SslMode). Em produção o TLS até o MySQL é obrigatório: habilite-o no MySQL ou informe -SslMode."
        }
        $SslMode = $atual.SslMode
    }
    Write-Host "MySQL: $ServidorMySql`:$PortaMySql, base '$BaseCatalogo', contas '$UsuarioCatalogo', '$UsuarioRuntime' e '$UsuarioAdmin' (SslMode=$SslMode)."

    foreach ($obrigatorio in @("portalbase-$amb-mysql-catalogo", "portalbase-$amb-chave-mfa", "portalbase-$amb-chave-hash", "portalbase-$amb-chave-auditoria")) {
        if (-not $atual.Segredos[$obrigatorio]) { throw "A configuração atual não tem o valor de '$obrigatorio'. Nada foi copiado para o cofre." }
    }
    foreach ($par in $atual.Segredos.GetEnumerator()) {
        if (-not $par.Value) { Write-Warning "$($par.Key): sem valor no ambiente atual (senha do MySQL é pedida abaixo)."; continue }
        if ((Test-Path -LiteralPath (Join-Path $pastas.Cofre ($par.Key + '.segredo'))) -and -not $Substituir) {
            Write-Host "$($par.Key): já está no cofre (mantido)."
            continue
        }
        & $cofreScript -Acao Definir -Diretorio $pastas.Cofre -Nome $par.Key -ValorSeguro (ConvertTo-SecureString $par.Value -AsPlainText -Force)
    }

    # Arquivos já enviados (planilhas): a base guarda o caminho RELATIVO; copia sem sobrescrever nada.
    if ($atual.DiretorioArquivos -and (Test-Path -LiteralPath $atual.DiretorioArquivos)) {
        & robocopy.exe $atual.DiretorioArquivos $pastas.Dados /E /XC /XN /XO /NFL /NDL /NJH /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Falha ao copiar os arquivos de $($atual.DiretorioArquivos) (robocopy $LASTEXITCODE)." }
        Write-Host "Arquivos enviados copiados de $($atual.DiretorioArquivos) para $($pastas.Dados)."
    }
    $atual = $null
}
if (-not $ServidorMySql) { throw 'Informe -ServidorMySql (ou use -MigrarDoAmbienteLocal).' }

# ---------------------------------------------------------------- segredos no cofre
Etapa 'Segredos no cofre'
$segredos = [ordered]@{
    MysqlCatalogo = "portalbase-$amb-mysql-catalogo"
    MysqlRuntime  = "portalbase-$amb-mysql-runtime"
    MysqlAdmin    = "portalbase-$amb-mysql-admin"
    ChaveMfa      = "portalbase-$amb-chave-mfa"
    ChaveHash     = "portalbase-$amb-chave-hash"
    ChaveAuditoria = "portalbase-$amb-chave-auditoria"
    ChaveBackup   = "portalbase-$amb-chave-backup"
}
foreach ($chave in @('ChaveMfa', 'ChaveHash', 'ChaveAuditoria', 'ChaveBackup')) {
    & $cofreScript -Acao GerarChave -Diretorio $pastas.Cofre -Nome $segredos[$chave]
}
$senhas = [ordered]@{
    MysqlCatalogo = "senha da conta '$UsuarioCatalogo' (catálogo)"
    MysqlRuntime  = "senha da conta '$UsuarioRuntime' (bases dos contribuintes)"
    MysqlAdmin    = "senha da conta '$UsuarioAdmin' (criar contribuintes, backup e restauração)"
}
foreach ($s in $senhas.GetEnumerator()) {
    $arquivo = Join-Path $pastas.Cofre ($segredos[$s.Key] + '.segredo')
    if (Test-Path -LiteralPath $arquivo) {
        Write-Host "$($segredos[$s.Key]): já está no cofre (para trocar: cofre-windows.ps1 -Acao Definir)."
    }
    else {
        Write-Host "Digite a $($s.Value):"
        & $cofreScript -Acao Definir -Diretorio $pastas.Cofre -Nome $segredos[$s.Key]
    }
}

# ---------------------------------------------------------------- configuração do servidor (nenhuma senha)
$comuns = [ordered]@{
    'ConnectionStrings__Catalogo'                   = "Server=$ServidorMySql;Port=$PortaMySql;Database=$BaseCatalogo;User Id=$UsuarioCatalogo;SslMode=$SslMode;"
    'BancoCatalogo__SenhaRef'                       = $segredos.MysqlCatalogo
    'Vault__Modo'                                   = 'CofreWindows'
    'Vault__DiretorioCofreWindows'                  = $pastas.Cofre
    'Vault__Ambiente'                               = $amb
    'BancoTenant__Servidor'                         = $ServidorMySql
    'BancoTenant__Porta'                            = "$PortaMySql"
    'BancoTenant__SslMode'                          = $SslMode
    'BancoTenant__RuntimeUsuario'                   = $UsuarioRuntime
    'BancoTenant__RuntimeSenhaRef'                  = $segredos.MysqlRuntime
    # Conta administrativa: a Web cria contribuintes e restaura; o Worker faz o backup agendado (mysqldump) com ela.
    'BancoTenant__AdminUsuario'                     = $UsuarioAdmin
    'BancoTenant__AdminSenhaRef'                    = $segredos.MysqlAdmin
    'SegurancaIdentidade__ChaveProtecaoMfaRef'      = $segredos.ChaveMfa
    'SegurancaIdentidade__ChaveHashRef'             = $segredos.ChaveHash
    'Auditoria__ChaveIntegridadeRef'                = $segredos.ChaveAuditoria
    'Backup__ChaveCriptografiaRef'                  = $segredos.ChaveBackup
    'Armazenamento__DiretorioRaiz'                  = $pastas.Dados
    'Armazenamento__DiretorioTemporario'            = $pastas.Tmp
    'ArmazenamentoBackup__DiretorioRaiz'            = $pastas.Backups
    'Observabilidade__LogEventosWindows__Habilitado' = 'true'
    # Log completo (Information em diante) em arquivo diario: o Log de Aplicativo so recebe avisos e erros (OBS-001).
    'Observabilidade__LogArquivo__Diretorio'        = $pastas.Logs
    'Observabilidade__LogArquivo__DiasRetencao'     = "$DiasRetencaoLog"
}
if ($AceitarUploadSemAntivirus) {
    # Decisão de risco explícita (ver .PARAMETER AceitarUploadSemAntivirus): aceita planilhas sem varredura.
    $comuns['Upload__AntivirusObrigatorio'] = 'false'
}

# ---------------------------------------------------------------- backup automático e cópia externa
# Sem isto o servidor ficava SEM backup automático (auditoria de 08/10/2026, BKP-002/BKP-003).
Etapa 'Backup automático'
function Localizar-BinarioMySql([string] $nome) {
    if ($DiretorioBinariosMySql) {
        $candidato = Join-Path $DiretorioBinariosMySql "$nome.exe"
        if (Test-Path -LiteralPath $candidato -PathType Leaf) { return (Resolve-Path -LiteralPath $candidato).Path }
        return $null
    }
    $comando = Get-Command "$nome.exe" -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($comando) { return $comando.Source }
    foreach ($raiz in @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }) {
        $pastaMySql = Join-Path $raiz 'MySQL'
        if (Test-Path -LiteralPath $pastaMySql) {
            $achado = Get-ChildItem -LiteralPath $pastaMySql -Recurse -Filter "$nome.exe" -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1
            if ($achado) { return $achado.FullName }
        }
    }
    return $null
}

$problemaBackup = $null
$mysqldump = Localizar-BinarioMySql 'mysqldump'
$mysqlCliente = Localizar-BinarioMySql 'mysql'
if ($mysqldump -and $mysqlCliente) {
    # Caminho COMPLETO: o serviço e o pool não enxergam o PATH do usuário que rodou este script.
    $comuns['Backup__ExecutorMySql__Habilitado'] = 'true'
    $comuns['Backup__ExecutorMySql__CaminhoMysqldump'] = $mysqldump
    $comuns['Backup__ExecutorMySql__CaminhoMysql'] = $mysqlCliente
    Write-Host "mysqldump: $mysqldump"
    Write-Host "mysql:     $mysqlCliente"
    if ($SemBackupAgendado) {
        Write-Warning 'Backup automático NÃO ligado (-SemBackupAgendado): só o backup pela tela. Operações vai mostrar o erro BackupAgendadoDesligado.'
    }
    else {
        $comuns['Backup__AgendamentoHabilitado'] = 'true'
        $comuns['Backup__HorarioAgendamentoUtc'] = $HorarioBackupUtc
        Write-Host "Backup automático LIGADO: todo dia às $HorarioBackupUtc UTC (catálogo, bases e arquivos de cada contribuinte, cifrados)."
        if ($SemWorker) { Write-Warning 'Sem o Worker (-SemWorker) o backup automático não roda neste servidor: ele é feito pelo serviço do Worker.' }
    }
}
else {
    $problemaBackup = 'mysqldump.exe e/ou mysql.exe não encontrados: o backup automático NÃO foi ligado. Instale o MySQL Server (ou as ferramentas de cliente) e rode de novo, ou informe -DiretorioBinariosMySql.'
    Write-Warning $problemaBackup
}

$problemaOffsite = $null
if ($DiretorioOffsite) {
    $ehRede = $DiretorioOffsite.StartsWith('\\')
    if (-not $ehRede -and -not [System.IO.Path]::IsPathRooted($DiretorioOffsite)) { throw 'DiretorioOffsite precisa ser um caminho completo (ex.: E:\CopiaPortalBase ou \\servidor\PortalBase).' }
    if ($ehRede) {
        if (-not (Test-Path -LiteralPath $DiretorioOffsite)) {
            $problemaOffsite = "o compartilhamento '$DiretorioOffsite' não respondeu agora. Confira o caminho e dê Modificar à conta do computador ($env:USERDOMAIN\$env:COMPUTERNAME$)."
        }
        else {
            Write-Host "Cópia externa em rede: dê Modificar à conta do computador ($env:USERDOMAIN\$env:COMPUTERNAME$) no compartilhamento e na pasta."
        }
    }
    else {
        $raizOffsite = [System.IO.Path]::GetPathRoot($DiretorioOffsite)
        if (-not (Test-Path -LiteralPath $raizOffsite)) { throw "O disco '$raizOffsite' de -DiretorioOffsite não existe neste servidor." }
        if ($raizOffsite -eq $raizDados) {
            $problemaOffsite = "a cópia externa está no MESMO disco dos backups ($raizOffsite): não protege contra a perda do disco. Use outro disco ou um compartilhamento de outro servidor."
        }
        Proteger-Pasta $DiretorioOffsite $ambos
    }
    $comuns['ArmazenamentoBackup__DiretorioOffsite'] = $DiretorioOffsite
    Write-Host "Cópia externa dos backups: $DiretorioOffsite (cada cópia conferida pelo SHA-256)."
    if ($problemaOffsite) { Write-Warning "Cópia externa: $problemaOffsite" }
}
else {
    $problemaOffsite = "sem -DiretorioOffsite os backups ficam só em $($pastas.Backups), no mesmo servidor: se ele se perder, os backups se perdem junto."
    Write-Warning "Cópia externa: $problemaOffsite"
}
$web = [ordered]@{
    'DataProtection__CaminhoChaves' = $pastas.Chaves
    'AllowedHosts'                  = $Dominio
}
if ($SemWorker) {
    # Sem o serviço do Worker neste servidor: a falta de batida não é problema (saúde e alertas).
    $web['Observabilidade__WorkerEsperado'] = 'false'
}
$problemaAlertas = $null
if ($EmailsAlerta) {
    # Destinatários dos alertas (OBS-004). Endereço não é segredo; o portal nunca o escreve no log.
    $enderecos = @($EmailsAlerta.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($endereco in $enderecos) {
        if ($endereco -notmatch '^[^@\s<>;,]+@[^@\s<>;,]+\.[^@\s<>;,]+$') { throw "EmailsAlerta: '$endereco' não é um endereço de e-mail válido." }
    }
    $web['Observabilidade__EmailsAlerta'] = ($enderecos -join ';')
    Write-Host "Alertas por e-mail para $($enderecos.Count) destinatário(s)."
} else {
    $problemaAlertas = 'sem -EmailsAlerta os alertas (Worker parado, backup atrasado, disco cheio, e-mail parado) vão só para o Visualizador de Eventos e Auditoria > Incidentes.'
    Write-Warning "Alertas: $problemaAlertas"
}
# Um arquivo por host em %ProgramData%\PortalBase, lido pelo site no IIS, pelo serviço do Worker e pelo .exe aberto à
# mão (num PowerShell de administrador). Só nomes, caminhos e REFERÊNCIAS ao cofre: o portal recusa subir se achar
# senha nele. Pasta: Administradores e SYSTEM gravam; o pool e o Worker só leem.
function Gravar-ConfiguracaoServidor([string] $arquivo, $pares) {
    $raiz = [ordered]@{}
    foreach ($par in $pares) {
        $partes = $par.Key -split '__'
        $no = $raiz
        for ($i = 0; $i -lt $partes.Length - 1; $i++) {
            if (-not $no.Contains($partes[$i])) { $no[$partes[$i]] = [ordered]@{} }
            $no = $no[$partes[$i]]
        }
        $no[$partes[$partes.Length - 1]] = [string] $par.Value
    }
    $temporario = "$arquivo.$([Guid]::NewGuid().ToString('N')).tmp"
    [System.IO.File]::WriteAllText($temporario, ($raiz | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporario -Destination $arquivo -Force
    Write-Host "Gravado: $arquivo"
}

$pastaConfiguracao = Join-Path $env:ProgramData 'PortalBase'
Etapa "Configuração do servidor em $pastaConfiguracao"
Proteger-Pasta $pastaConfiguracao (@($contaPool, $contaWorker) | Where-Object { $_ }) 'ReadAndExecute'
Gravar-ConfiguracaoServidor (Join-Path $pastaConfiguracao 'PortalBase.Web.json') (@($comuns.GetEnumerator()) + @($web.GetEnumerator()))
if (-not $SemWorker) {
    Gravar-ConfiguracaoServidor (Join-Path $pastaConfiguracao 'PortalBase.Worker.json') @($comuns.GetEnumerator())
}

# Pool: só o ambiente. O que versões anteriores deste script gravavam aqui é removido (prevaleceria sobre o arquivo).
Etapa "Variáveis do pool '$Pool'"
$filtro = "system.applicationHost/applicationPools/add[@name='$Pool']/environmentVariables"
foreach ($nome in @($comuns.Keys) + @($web.Keys) + @('ASPNETCORE_ENVIRONMENT')) {
    $existe = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "$filtro/add[@name='$nome']" -Name 'value' -ErrorAction SilentlyContinue
    if ($null -ne $existe) {
        Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filtro -Name '.' -AtElement @{ name = $nome }
    }
}
Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filtro -Name '.' -Value @{ name = 'ASPNETCORE_ENVIRONMENT'; value = 'Production' }
Write-Host 'ASPNETCORE_ENVIRONMENT=Production'

if ($contaWorker) {
    Etapa "Variáveis do serviço '$ServicoWorker'"
    New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServicoWorker" -Name Environment -PropertyType MultiString -Value ([string[]] @('DOTNET_ENVIRONMENT=Production')) -Force | Out-Null
    Write-Host 'DOTNET_ENVIRONMENT=Production'
}

# ---------------------------------------------------------------- reinício
Etapa 'Reiniciando'
if ((Get-WebAppPoolState -Name $Pool).Value -eq 'Started') { Restart-WebAppPool -Name $Pool } else { Start-WebAppPool -Name $Pool }
if ($contaWorker) {
    $inicioServico = Get-Date
    Restart-Service -Name $ServicoWorker -ErrorAction Continue
    Start-Sleep -Seconds 20
    $estado = (Get-Service -Name $ServicoWorker).Status
    if ($estado -eq 'Running') {
        Write-Host "Serviço '$ServicoWorker' em execução."
    }
    else {
        Write-Warning "O serviço '$ServicoWorker' não ficou em execução (estado: $estado). Últimos erros registrados:"
        $erros = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $FonteWorker, '.NET Runtime'; StartTime = $inicioServico } -MaxEvents 3 -ErrorAction SilentlyContinue)
        if ($erros.Count -eq 0) { Write-Host '  (nenhum; veja Visualizador de Eventos > Logs do Windows > Aplicativo e Sistema)' }
        foreach ($e in $erros) { Write-Host "  [$($e.TimeCreated)] $($e.ProviderName): $($e.Message)" }
    }
}

# ---------------------------------------------------------------- antivírus (Microsoft Defender)
# O portal procura o MpCmdRun.exe do mesmo jeito: a versão mais nova da plataforma e, sem ela, a pasta clássica.
function Localizar-MpCmdRun {
    $plataforma = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    if (Test-Path -LiteralPath $plataforma) {
        $versoes = @(Get-ChildItem -LiteralPath $plataforma -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                $partes = $_.Name.Split('-')
                $versao = $null
                $exe = Join-Path $_.FullName 'MpCmdRun.exe'
                if ([version]::TryParse($partes[0], [ref] $versao) -and (Test-Path -LiteralPath $exe)) {
                    [pscustomobject] @{ Versao = $versao; Nome = $_.Name; Exe = $exe }
                }
            } | Sort-Object Versao, Nome -Descending)
        if ($versoes.Count -gt 0) { return $versoes[0].Exe }
    }
    $classico = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    if (Test-Path -LiteralPath $classico) { return $classico }
    return $null
}

# Devolve $null se o Defender varreu um arquivo inofensivo sem erro; senão, o motivo (sem caminho nem conteúdo).
function Testar-WindowsDefender([string] $pastaTemporaria) {
    $mp = Localizar-MpCmdRun
    if (-not $mp) { return 'o MpCmdRun.exe do Microsoft Defender Antivirus não foi encontrado neste servidor.' }
    $estado = Get-MpComputerStatus -ErrorAction SilentlyContinue
    if ($estado -and -not $estado.AntivirusEnabled) {
        return "o Microsoft Defender Antivirus está desligado (modo: $($estado.AMRunningMode))."
    }
    $teste = Join-Path $pastaTemporaria ('teste-defender-{0}.txt' -f [guid]::NewGuid().ToString('N'))
    try {
        Set-Content -LiteralPath $teste -Value 'Portal Base: arquivo inofensivo para testar a varredura.' -Encoding UTF8
        & $mp -Scan -ScanType 3 -File $teste -DisableRemediation | Out-Null
        $codigo = $LASTEXITCODE
    }
    finally { Remove-Item -LiteralPath $teste -Force -ErrorAction SilentlyContinue }
    if ($codigo -ne 0) { return "a varredura de teste do Defender terminou com o código $codigo." }
    return $null
}

Etapa 'Antivírus (Microsoft Defender)'
$problemaDefender = Testar-WindowsDefender $pastas.Tmp
if ($problemaDefender) { Write-Warning "Antivírus: $problemaDefender" }
else { Write-Host 'Microsoft Defender respondeu (varredura de teste limpa): o portal vai varrer cada planilha enviada.' }

# ---------------------------------------------------------------- primeiro administrador (opcional)
if ($CriarPrimeiroAdministrador) {
    Etapa 'Primeiro Administrador da plataforma'
    if (-not $CaminhoSite) { throw 'Informe -CaminhoSite (pasta publicada da Web).' }
    $exe = Join-Path $CaminhoSite 'PortalBase.Web.exe'
    $dll = Join-Path $CaminhoSite 'PortalBase.Web.dll'
    if (-not (Test-Path -LiteralPath $exe) -and -not (Test-Path -LiteralPath $dll)) { throw "Não achei PortalBase.Web.exe nem PortalBase.Web.dll em $CaminhoSite." }
    # Lê o mesmo arquivo do servidor gravado acima (este PowerShell é de administrador).
    $env:ASPNETCORE_ENVIRONMENT = 'Production'

    Push-Location $CaminhoSite
    try {
        if (Test-Path -LiteralPath $exe) { & $exe criar-primeiro-administrador } else { & dotnet $dll criar-primeiro-administrador }
    }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { Write-Warning "O comando terminou com código $LASTEXITCODE (veja a mensagem acima)." }
}

Write-Host ''
if ($problemaDefender -and -not $AceitarUploadSemAntivirus) {
    Write-Warning ('Sem o Microsoft Defender, o envio de planilhas (Dados > Planilhas) vai responder "Arquivo rejeitado" ' +
        'até o Defender voltar. Se a decisão de risco for aceitar sem varredura, rode de novo com -AceitarUploadSemAntivirus.')
}
if ($problemaBackup) { Write-Warning "Backup: $problemaBackup" }
if ($problemaOffsite) { Write-Warning "Cópia externa: $problemaOffsite" }
if ($problemaAlertas) { Write-Warning "Alertas: $problemaAlertas" }
Write-Warning ('Recuperação de desastre: exporte o cofre AGORA e sempre que um segredo mudar - sem o pacote (e a senha dele) ' +
    'nenhum backup decifra se este servidor se perder: .\cofre-windows.ps1 -Acao Exportar -Diretorio ' + $pastas.Cofre +
    ' -Arquivo <pendrive ou cofre externo>\cofre-portalbase.portalbasecofre')
Write-Host 'Pronto. Confira:' -ForegroundColor Green
Write-Host "  - abra https://$($Dominio.Split(';')[0])/ - a tela de login deve aparecer;"
Write-Host "  - se não abrir: Visualizador de Eventos > Logs do Windows > Aplicativo (origens 'IIS AspNetCore Module V2' e '$FonteWeb');"
Write-Host "  - Worker: services.msc > '$ServicoWorker' em execução; avisos e erros dele no mesmo log, origem '$FonteWorker';"
Write-Host "  - antivírus: envie uma planilha em Dados > Planilhas; se vier 'Arquivo rejeitado', veja o log, origem '$FonteWeb';"
Write-Host '  - segredos cadastrados: .\cofre-windows.ps1 -Acao Listar -Diretorio ' $pastas.Cofre
Write-Host '  - e-mail e segredos de integração: pelas telas do portal (vão para o mesmo cofre).'
Write-Host '  - backups: Operações > Backups (o primeiro automático sai no próximo horário; "Fazer backup agora" testa já);'
Write-Host '  - recuperação: docs\runbooks\DISASTER-RECOVERY.md (ensaie uma vez por trimestre).'
