#Requires -Version 5.1
<#
.SYNOPSIS
    Administra o COFRE LOCAL DO WINDOWS do Portal Base (Vault:Modo=CofreWindows) num servidor próprio (IIS).

.DESCRIPTION
    Cada segredo é um arquivo "<nome>.segredo" numa pasta privada, cifrado com o DPAPI da MÁQUINA (só este servidor
    decifra). Quem pode ler é decidido pela ACL da pasta: a conta do pool do IIS, a do serviço do Worker e os
    administradores. O formato é o mesmo lido pelo portal (PortalBase.Infrastructure/Segredos/ProvedorSegredosCofreWindows).

    Nenhum valor é exibido, gravado em log ou aceito como texto na linha de comando: senhas são digitadas sem eco
    (ou passadas como SecureString), chaves são geradas aqui e arquivos (PFX) são lidos do disco.

    Execute como ADMINISTRADOR, no próprio servidor.

.PARAMETER Acao
    Preparar      cria a pasta e aplica a ACL (só administradores, SYSTEM e as contas em -Contas).
    Definir       grava/troca um segredo digitado (senha do MySQL, client_secret...).
    GerarChave    gera uma chave aleatória de 32 bytes (Base64) - chaves do MFA, do hash, da auditoria e dos backups.
                  Não troca uma chave existente (trocar invalida dados já gravados) sem -Substituir.
    ImportarArquivo grava o conteúdo de um arquivo em Base64 (ex.: PFX), lido de -Arquivo.
    Listar        mostra só os NOMES dos segredos.
    Testar        confere se o segredo abre neste servidor (mostra só o tamanho).
    Remover       apaga um segredo.
    Exportar      grava TODOS os segredos num pacote (-Arquivo, extensão .portalbasecofre) protegido por uma SENHA digitada
                  (mín. 16 caracteres). É o que permite recuperar os backups se este servidor se perder: o DPAPI só
                  abre os arquivos do cofre nesta máquina. Guarde o pacote e a senha SEPARADOS, fora do servidor e
                  longe dos backups (ex.: pacote num cofre físico/pendrive; senha no gerenciador de senhas da empresa).
                  Refaça a exportação sempre que um segredo mudar (senha do SMTP, segredos de integração...).
    Restaurar     regrava no cofre DESTA máquina os segredos de um pacote (-Arquivo). Não troca um segredo que já
                  existe sem -Substituir. Depois: -Acao Preparar (ACL) se a pasta for nova.

.EXAMPLE
    .\cofre-windows.ps1 -Acao Preparar -Diretorio D:\PortalBase\cofre -Contas 'IIS AppPool\PortalBase','NT SERVICE\PortalBase.Worker'
    .\cofre-windows.ps1 -Acao Definir -Diretorio D:\PortalBase\cofre -Nome portalbase-prod-mysql-catalogo
    .\cofre-windows.ps1 -Acao GerarChave -Diretorio D:\PortalBase\cofre -Nome portalbase-prod-chave-mfa
    .\cofre-windows.ps1 -Acao Exportar -Diretorio D:\PortalBase\cofre -Arquivo E:\cofre-portalbase-2026-10.portalbasecofre
    .\cofre-windows.ps1 -Acao Restaurar -Diretorio D:\PortalBase\cofre -Arquivo E:\cofre-portalbase-2026-10.portalbasecofre
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preparar', 'Definir', 'GerarChave', 'ImportarArquivo', 'Listar', 'Testar', 'Remover', 'Exportar', 'Restaurar')]
    [string] $Acao,

    [Parameter(Mandatory = $true)]
    [string] $Diretorio,

    [string] $Nome,

    [string[]] $Contas = @(),

    [string] $Arquivo,

    [System.Security.SecureString] $ValorSeguro,

    [System.Security.SecureString] $SenhaPacote,

    [switch] $Substituir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Cabecalho = 'COFRE1:'
$Extensao = '.segredo'

function Carregar-Dpapi {
    if (-not ('System.Security.Cryptography.ProtectedData' -as [type])) {
        try { Add-Type -AssemblyName System.Security } catch { }
    }
    if (-not ('System.Security.Cryptography.ProtectedData' -as [type])) {
        try { Add-Type -AssemblyName System.Security.Cryptography.ProtectedData } catch { }
    }
    if (-not ('System.Security.Cryptography.ProtectedData' -as [type])) {
        throw 'DPAPI indisponível neste PowerShell. Use o Windows PowerShell 5.1 (powershell.exe).'
    }
}

function Nome-Valido([string] $valor) {
    if ([string]::IsNullOrWhiteSpace($valor)) { throw 'Informe -Nome.' }
    $n = $valor.Trim()
    if ($n.Length -gt 127 -or $n -notmatch '^[A-Za-z0-9-]+$') {
        throw 'Nome de segredo inválido (use letras, dígitos e hífen, até 127).'
    }
    return $n.ToLowerInvariant()
}

function Entropia([string] $nome) {
    return [System.Text.Encoding]::UTF8.GetBytes("PortalBase.CofreWindows.v1:$nome")
}

function Caminho([string] $nome) {
    return (Join-Path $Diretorio ($nome + $Extensao))
}

function Exigir-Pasta {
    if (-not (Test-Path -LiteralPath $Diretorio -PathType Container)) {
        throw "A pasta do cofre não existe. Rode primeiro: -Acao Preparar -Diretorio '$Diretorio'"
    }
}

function Gravar([string] $nome, [byte[]] $bytes) {
    Carregar-Dpapi
    Exigir-Pasta
    $cifrado = $null
    try {
        $cifrado = [System.Security.Cryptography.ProtectedData]::Protect(
            $bytes, (Entropia $nome), [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        $conteudo = $Cabecalho + [Convert]::ToBase64String($cifrado)
        $destino = Caminho $nome
        $temporario = Join-Path $Diretorio ("$nome." + [Guid]::NewGuid().ToString('N') + '.tmp')
        [System.IO.File]::WriteAllText($temporario, $conteudo, [System.Text.Encoding]::ASCII)
        Move-Item -LiteralPath $temporario -Destination $destino -Force
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        if ($null -ne $cifrado) { [Array]::Clear($cifrado, 0, $cifrado.Length) }
    }
    Write-Host "Segredo '$nome' gravado no cofre."
}

function Texto-De([System.Security.SecureString] $seguro) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($seguro)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

# ---------------------------------------------------------------- pacote exportado (.portalbasecofre v1)
# Formato lido também pelo portal (PortalBase.Infrastructure/Segredos/PacoteCofreExportado.cs):
#   "COFREPKG01" | iterações (int32 big-endian) | sal (16) | IV (16) | AES-256-CBC/PKCS7 | HMAC-SHA256 de tudo antes
#   chaves = PBKDF2-HMAC-SHA256(senha, sal, iterações, 64 bytes): [0..32) cifra, [32..64) HMAC
#   texto claro (UTF-8): "COFRE v1" e uma linha "<nome>:<Base64 do valor>" por segredo
$MagicoPacote = [System.Text.Encoding]::ASCII.GetBytes('COFREPKG01')
$IteracoesPacote = 600000

function Senha-Do-Pacote([bool] $nova) {
    if ($null -ne $SenhaPacote) {
        $texto = Texto-De $SenhaPacote
    }
    else {
        $texto = Texto-De (Read-Host -AsSecureString 'Senha do pacote (não aparece na tela)')
        if ($nova) {
            $repetida = Texto-De (Read-Host -AsSecureString 'Repita a senha do pacote')
            if ($texto -cne $repetida) { throw 'As senhas digitadas são diferentes. Nada foi feito.' }
        }
    }
    if ([string]::IsNullOrEmpty($texto)) { throw 'Senha do pacote vazia. Nada foi feito.' }
    if ($nova -and $texto.Length -lt 16) { throw 'A senha do pacote precisa ter pelo menos 16 caracteres. Nada foi feito.' }
    return $texto
}

function Derivar-Chaves([string] $senha, [byte[]] $sal, [int] $iteracoes) {
    $pbkdf2 = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        [System.Text.Encoding]::UTF8.GetBytes($senha), $sal, $iteracoes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { return , $pbkdf2.GetBytes(64) } finally { $pbkdf2.Dispose() }
}

function Inteiro-BigEndian([int] $valor) {
    $bytes = [BitConverter]::GetBytes($valor)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    return , $bytes
}

function Proteger-Pacote([byte[]] $claro, [string] $senha, [int] $iteracoes) {
    $sal = New-Object byte[] 16
    $iv = New-Object byte[] 16
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($sal); $rng.GetBytes($iv) } finally { $rng.Dispose() }
    $chaves = Derivar-Chaves $senha $sal $iteracoes
    $chaveCifra = New-Object byte[] 32
    $chaveMac = New-Object byte[] 32
    [Array]::Copy($chaves, 0, $chaveCifra, 0, 32)
    [Array]::Copy($chaves, 32, $chaveMac, 0, 32)
    $aes = [System.Security.Cryptography.Aes]::Create()
    $hmac = New-Object System.Security.Cryptography.HMACSHA256(, $chaveMac)
    $corpo = New-Object System.IO.MemoryStream
    try {
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $cifrador = $aes.CreateEncryptor($chaveCifra, $iv)
        try { $cifrado = $cifrador.TransformFinalBlock($claro, 0, $claro.Length) } finally { $cifrador.Dispose() }
        $it = Inteiro-BigEndian $iteracoes
        foreach ($parte in @($MagicoPacote, $it, $sal, $iv, $cifrado)) { $corpo.Write($parte, 0, $parte.Length) }
        $mac = $hmac.ComputeHash($corpo.ToArray())
        $corpo.Write($mac, 0, $mac.Length)
        return , $corpo.ToArray()
    }
    finally {
        $aes.Dispose(); $hmac.Dispose(); $corpo.Dispose()
        [Array]::Clear($chaves, 0, $chaves.Length); [Array]::Clear($chaveCifra, 0, 32); [Array]::Clear($chaveMac, 0, 32)
    }
}

function Abrir-Pacote([byte[]] $pacote, [string] $senha) {
    $inicio = $MagicoPacote.Length + 4 + 16 + 16
    if ($pacote.Length -lt ($inicio + 16 + 32)) { throw 'O arquivo não é um pacote do cofre do Portal Base (.portalbasecofre v1).' }
    for ($i = 0; $i -lt $MagicoPacote.Length; $i++) {
        if ($pacote[$i] -ne $MagicoPacote[$i]) { throw 'O arquivo não é um pacote do cofre do Portal Base (.portalbasecofre v1).' }
    }
    $it = New-Object byte[] 4
    [Array]::Copy($pacote, $MagicoPacote.Length, $it, 0, 4)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($it) }
    $iteracoes = [BitConverter]::ToInt32($it, 0)
    $tamanhoCifrado = $pacote.Length - $inicio - 32
    if ($iteracoes -lt 100000 -or $iteracoes -gt 10000000 -or ($tamanhoCifrado % 16) -ne 0) { throw 'Pacote do cofre malformado.' }
    $sal = New-Object byte[] 16
    $iv = New-Object byte[] 16
    $cifrado = New-Object byte[] $tamanhoCifrado
    $macGravado = New-Object byte[] 32
    [Array]::Copy($pacote, $MagicoPacote.Length + 4, $sal, 0, 16)
    [Array]::Copy($pacote, $MagicoPacote.Length + 20, $iv, 0, 16)
    [Array]::Copy($pacote, $inicio, $cifrado, 0, $tamanhoCifrado)
    [Array]::Copy($pacote, $pacote.Length - 32, $macGravado, 0, 32)

    $chaves = Derivar-Chaves $senha $sal $iteracoes
    $chaveCifra = New-Object byte[] 32
    $chaveMac = New-Object byte[] 32
    [Array]::Copy($chaves, 0, $chaveCifra, 0, 32)
    [Array]::Copy($chaves, 32, $chaveMac, 0, 32)
    $hmac = New-Object System.Security.Cryptography.HMACSHA256(, $chaveMac)
    $aes = [System.Security.Cryptography.Aes]::Create()
    $claro = $null
    try {
        $esperado = $hmac.ComputeHash($pacote, 0, $pacote.Length - 32)
        $diferenca = 0
        for ($i = 0; $i -lt 32; $i++) { $diferenca = $diferenca -bor ($esperado[$i] -bxor $macGravado[$i]) } # tempo constante
        if ($diferenca -ne 0) { throw 'Senha do pacote incorreta ou pacote alterado. Nada foi gravado.' }

        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $decifrador = $aes.CreateDecryptor($chaveCifra, $iv)
        try { $claro = $decifrador.TransformFinalBlock($cifrado, 0, $cifrado.Length) } finally { $decifrador.Dispose() }
        $linhas = [System.Text.Encoding]::UTF8.GetString($claro).Split([char[]] @([char] 10), [StringSplitOptions]::RemoveEmptyEntries)
        if ($linhas.Length -eq 0 -or $linhas[0].Trim() -cne 'COFRE v1') { throw 'Pacote do cofre com conteúdo desconhecido.' }
        $segredosPacote = [ordered]@{}
        for ($i = 1; $i -lt $linhas.Length; $i++) {
            $linha = $linhas[$i].Trim()
            $separador = $linha.IndexOf(':')
            if ($separador -le 0) { throw 'Pacote do cofre com conteúdo desconhecido.' }
            $segredosPacote[(Nome-Valido $linha.Substring(0, $separador))] = [Convert]::FromBase64String($linha.Substring($separador + 1))
        }
        return $segredosPacote
    }
    finally {
        $hmac.Dispose(); $aes.Dispose()
        [Array]::Clear($chaves, 0, $chaves.Length); [Array]::Clear($chaveCifra, 0, 32); [Array]::Clear($chaveMac, 0, 32)
        if ($null -ne $claro) { [Array]::Clear($claro, 0, $claro.Length) }
    }
}

switch ($Acao) {
    'Preparar' {
        New-Item -ItemType Directory -Path $Diretorio -Force | Out-Null
        $acl = New-Object System.Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false) # sem herança: só as regras abaixo
        $heranca = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        $propagacao = [System.Security.AccessControl.PropagationFlags]::None
        foreach ($sid in @('S-1-5-32-544', 'S-1-5-18')) { # Administradores e SYSTEM (independe do idioma do Windows)
            $conta = (New-Object System.Security.Principal.SecurityIdentifier($sid)).Translate([System.Security.Principal.NTAccount])
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($conta, 'FullControl', $heranca, $propagacao, 'Allow')))
        }
        foreach ($c in $Contas) {
            if ([string]::IsNullOrWhiteSpace($c)) { continue }
            # Modify: a Web grava os segredos definidos pelas telas (senha do SMTP, segredos de integração).
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($c.Trim(), 'Modify', $heranca, $propagacao, 'Allow')))
        }
        Set-Acl -LiteralPath $Diretorio -AclObject $acl
        Write-Host "Pasta do cofre pronta: $Diretorio (acesso: Administradores, SYSTEM$(if ($Contas.Count -gt 0) { ', ' + ($Contas -join ', ') }))."
    }
    'Definir' {
        $n = Nome-Valido $Nome
        if ($null -eq $ValorSeguro) {
            $primeiro = Read-Host -AsSecureString "Valor de '$n' (não aparece na tela)"
            $segundo = Read-Host -AsSecureString 'Repita o valor'
            $a = Texto-De $primeiro
            $b = Texto-De $segundo
            if ($a -cne $b) { throw 'Os valores digitados são diferentes. Nada foi gravado.' }
            $ValorSeguro = $primeiro
        }
        $texto = Texto-De $ValorSeguro
        if ([string]::IsNullOrEmpty($texto)) { throw 'Valor vazio. Nada foi gravado.' }
        Gravar $n ([System.Text.Encoding]::UTF8.GetBytes($texto))
    }
    'GerarChave' {
        $n = Nome-Valido $Nome
        if ((Test-Path -LiteralPath (Caminho $n)) -and -not $Substituir) {
            Write-Host "A chave '$n' já existe e foi mantida (trocar invalida dados já gravados; use -Substituir só se tiver certeza)."
            break
        }
        $chave = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($chave) } finally { $rng.Dispose() }
        $base64 = [Convert]::ToBase64String($chave)
        [Array]::Clear($chave, 0, $chave.Length)
        Gravar $n ([System.Text.Encoding]::UTF8.GetBytes($base64))
    }
    'ImportarArquivo' {
        $n = Nome-Valido $Nome
        if ([string]::IsNullOrWhiteSpace($Arquivo) -or -not (Test-Path -LiteralPath $Arquivo -PathType Leaf)) { throw 'Informe -Arquivo com um arquivo existente.' }
        $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Arquivo).Path)
        $base64 = [Convert]::ToBase64String($bytes)
        [Array]::Clear($bytes, 0, $bytes.Length)
        Gravar $n ([System.Text.Encoding]::UTF8.GetBytes($base64))
    }
    'Listar' {
        Exigir-Pasta
        Get-ChildItem -LiteralPath $Diretorio -Filter ('*' + $Extensao) | Sort-Object Name |
            ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Name) }
    }
    'Testar' {
        Carregar-Dpapi
        $n = Nome-Valido $Nome
        $arquivoSegredo = Caminho $n
        if (-not (Test-Path -LiteralPath $arquivoSegredo)) { throw "Segredo '$n' não encontrado." }
        $conteudo = [System.IO.File]::ReadAllText($arquivoSegredo).Trim()
        if (-not $conteudo.StartsWith($Cabecalho)) { throw "Segredo '$n' com formato desconhecido." }
        $aberto = [System.Security.Cryptography.ProtectedData]::Unprotect(
            [Convert]::FromBase64String($conteudo.Substring($Cabecalho.Length)), (Entropia $n), [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        Write-Host "Segredo '$n' abre neste servidor ($($aberto.Length) bytes)."
        [Array]::Clear($aberto, 0, $aberto.Length)
    }
    'Exportar' {
        Carregar-Dpapi
        Exigir-Pasta
        if ([string]::IsNullOrWhiteSpace($Arquivo)) { throw 'Informe -Arquivo com o caminho do pacote a gravar (ex.: E:\cofre-portalbase.portalbasecofre).' }
        $destinoPacote = [System.IO.Path]::GetFullPath($Arquivo)
        if ((Test-Path -LiteralPath $destinoPacote) -and -not $Substituir) { throw "O arquivo '$destinoPacote' já existe (use outro nome ou -Substituir)." }
        $arquivosSegredo = @(Get-ChildItem -LiteralPath $Diretorio -Filter ('*' + $Extensao) | Sort-Object Name)
        if ($arquivosSegredo.Count -eq 0) { throw 'O cofre está vazio. Nada foi exportado.' }
        $senhaNova = Senha-Do-Pacote $true
        $texto = New-Object System.Text.StringBuilder
        [void] $texto.Append("COFRE v1`n")
        foreach ($item in $arquivosSegredo) {
            $n = Nome-Valido ([System.IO.Path]::GetFileNameWithoutExtension($item.Name))
            $conteudo = [System.IO.File]::ReadAllText($item.FullName).Trim()
            if (-not $conteudo.StartsWith($Cabecalho)) { throw "Segredo '$n' com formato desconhecido. Nada foi exportado." }
            $aberto = [System.Security.Cryptography.ProtectedData]::Unprotect(
                [Convert]::FromBase64String($conteudo.Substring($Cabecalho.Length)), (Entropia $n), [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
            [void] $texto.Append($n).Append(':').Append([Convert]::ToBase64String($aberto)).Append("`n")
            [Array]::Clear($aberto, 0, $aberto.Length)
        }
        $claroPacote = [System.Text.Encoding]::UTF8.GetBytes($texto.ToString())
        [void] $texto.Clear()
        try { $pacote = Proteger-Pacote $claroPacote $senhaNova $IteracoesPacote }
        finally { [Array]::Clear($claroPacote, 0, $claroPacote.Length) }
        $pastaPacote = [System.IO.Path]::GetDirectoryName($destinoPacote)
        if (-not (Test-Path -LiteralPath $pastaPacote)) { New-Item -ItemType Directory -Path $pastaPacote -Force | Out-Null }
        $temporarioPacote = "$destinoPacote.$([Guid]::NewGuid().ToString('N')).tmp"
        [System.IO.File]::WriteAllBytes($temporarioPacote, $pacote)
        Move-Item -LiteralPath $temporarioPacote -Destination $destinoPacote -Force
        Write-Host "Pacote do cofre gravado: $destinoPacote ($($arquivosSegredo.Count) segredo(s))."
        Write-Host 'Guarde o pacote e a SENHA separados, fora deste servidor e longe dos backups. Sem os dois, nenhum backup decifra se o servidor se perder.'
        Write-Host 'Refaça a exportação sempre que um segredo mudar (senha do SMTP, segredos de integração, senhas do MySQL).'
    }
    'Restaurar' {
        Exigir-Pasta
        if ([string]::IsNullOrWhiteSpace($Arquivo) -or -not (Test-Path -LiteralPath $Arquivo -PathType Leaf)) { throw 'Informe -Arquivo com o pacote .portalbasecofre.' }
        $pacote = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Arquivo).Path)
        $segredosPacote = Abrir-Pacote $pacote (Senha-Do-Pacote $false)
        $gravados = 0
        $mantidos = 0
        foreach ($par in $segredosPacote.GetEnumerator()) {
            if ((Test-Path -LiteralPath (Caminho $par.Key)) -and -not $Substituir) {
                Write-Host "Segredo '$($par.Key)' já existe neste cofre (mantido; use -Substituir para trocar)."
                [Array]::Clear($par.Value, 0, $par.Value.Length)
                $mantidos++
                continue
            }
            Gravar $par.Key $par.Value
            $gravados++
        }
        Write-Host "Restauração do cofre concluída: $gravados gravado(s), $mantidos mantido(s). Confira com -Acao Testar e aplique a ACL com -Acao Preparar se a pasta for nova."
    }
    'Remover' {
        $n = Nome-Valido $Nome
        $arquivoSegredo = Caminho $n
        if (Test-Path -LiteralPath $arquivoSegredo) {
            Remove-Item -LiteralPath $arquivoSegredo -Force
            Write-Host "Segredo '$n' removido."
        }
        else {
            Write-Host "Segredo '$n' não existe."
        }
    }
}
