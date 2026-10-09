using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PortalBase.Application.Abstracoes;

namespace PortalBase.Infrastructure.Segredos;

/// <summary>Cifra/decifra o conteúdo de um segredo do cofre local (DPAPI no Windows; trocável nos testes).</summary>
public interface IProtetorCofreLocal
{
    byte[] Proteger(byte[] dados, byte[] entropia);

    byte[] Desproteger(byte[] protegido, byte[] entropia);
}

/// <summary>
/// DPAPI do Windows com escopo de MÁQUINA: só este servidor decifra (arquivo copiado para outra máquina não abre). Quem
/// pode ler o arquivo é decidido pela ACL da pasta do cofre (conta do pool do IIS, conta do Worker e administradores).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProtetorDpapiMaquina : IProtetorCofreLocal
{
    public byte[] Proteger(byte[] dados, byte[] entropia) => ProtectedData.Protect(dados, entropia, DataProtectionScope.LocalMachine);

    public byte[] Desproteger(byte[] protegido, byte[] entropia) => ProtectedData.Unprotect(protegido, entropia, DataProtectionScope.LocalMachine);
}

/// <summary>
/// Formato dos arquivos do cofre local — o MESMO usado por <c>scripts/cofre-windows.ps1</c>. Um arquivo por segredo,
/// <c>&lt;nome&gt;.segredo</c> (nome em minúsculas), contendo <c>COFRE1:</c> + Base64 do conteúdo cifrado. A entropia
/// amarra o conteúdo ao nome (trocar arquivos de lugar não troca segredos).
/// </summary>
public static class FormatoCofreWindows
{
    public const string Extensao = ".segredo";
    public const string Cabecalho = "COFRE1:";
    private const int TamanhoMaximoNome = 127;
    private const int TamanhoMaximoArquivo = 2 * 1024 * 1024;

    /// <summary>Mesmas regras de nome do Key Vault: letras, dígitos e hífen, até 127 (impede caminho/travessia).</summary>
    public static string NomeValido(string referencia)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencia);
        var nome = referencia.Trim();
        if (nome.Length > TamanhoMaximoNome || !nome.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new InvalidOperationException("Nome de segredo inválido (use letras, dígitos e hífen, até 127).");
        }

        return nome.ToLowerInvariant();
    }

    public static byte[] Entropia(string nomeValido) => Encoding.UTF8.GetBytes($"PortalBase.CofreWindows.v1:{nomeValido}");

    public static string Caminho(string diretorio, string nomeValido) => Path.Combine(diretorio, nomeValido + Extensao);

    public static string Serializar(byte[] protegido) => Cabecalho + Convert.ToBase64String(protegido);

    public static byte[] Ler(string conteudo, string nomeValido)
    {
        ArgumentNullException.ThrowIfNull(conteudo);
        var texto = conteudo.Trim();
        if (texto.Length > TamanhoMaximoArquivo || !texto.StartsWith(Cabecalho, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Segredo '{nomeValido}' com formato desconhecido no cofre local.");
        }

        try
        {
            return Convert.FromBase64String(texto[Cabecalho.Length..]);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Segredo '{nomeValido}' com formato desconhecido no cofre local.");
        }
    }
}

/// <summary>
/// COFRE LOCAL DO WINDOWS (<c>Vault:Modo=CofreWindows</c>) — alternativa ao Azure Key Vault para servidor próprio (IIS),
/// aceita em Produção. Cada segredo é um arquivo cifrado com DPAPI da máquina numa pasta privada
/// (<c>Vault:DiretorioCofreWindows</c>) cuja ACL só libera a conta do pool do IIS, a do Worker e os administradores.
/// Os segredos de operação (senhas do MySQL, chaves) são cadastrados por <c>scripts/cofre-windows.ps1</c>; os definidos
/// pelas telas (senha do SMTP, segredos de integração) são gravados aqui pela Web. Fail-closed: segredo ausente,
/// ilegível ou de outra máquina é erro — nunca valor vazio. O valor nunca é logado nem aparece em mensagem.
/// </summary>
public sealed class ProvedorSegredosCofreWindows : IProvedorSegredos, IEscritorSegredos
{
    // Uma gravação por vez no processo (a troca do arquivo é atômica de qualquer forma).
    private static readonly SemaphoreSlim TravaEscrita = new(1, 1);

    private readonly string _diretorio;
    private readonly IProtetorCofreLocal _protetor;

    public ProvedorSegredosCofreWindows(string diretorio, IProtetorCofreLocal protetor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diretorio);
        ArgumentNullException.ThrowIfNull(protetor);
        if (!Directory.Exists(diretorio))
        {
            throw new InvalidOperationException("A pasta do cofre local (Vault:DiretorioCofreWindows) não existe. Rode scripts/cofre-windows.ps1 -Acao Preparar.");
        }

        _diretorio = Path.GetFullPath(diretorio);
        _protetor = protetor;
    }

    public async ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default)
    {
        var nome = FormatoCofreWindows.NomeValido(referencia);
        var caminho = FormatoCofreWindows.Caminho(_diretorio, nome);
        string conteudo;
        try
        {
            conteudo = await File.ReadAllTextAsync(caminho, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException($"Segredo '{nome}' não encontrado no cofre.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Sem permissão para ler o segredo '{nome}' (confira a ACL da pasta do cofre).");
        }

        var protegido = FormatoCofreWindows.Ler(conteudo, nome);
        try
        {
            return Encoding.UTF8.GetString(_protetor.Desproteger(protegido, FormatoCofreWindows.Entropia(nome)));
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException($"O segredo '{nome}' não pôde ser aberto neste servidor (cadastrado em outra máquina?). Cadastre-o de novo.");
        }
    }

    /// <summary>Define/rotaciona: grava num arquivo temporário da mesma pasta e troca de uma vez (nunca deixa meio arquivo).</summary>
    public async ValueTask DefinirAsync(string referencia, string valor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(valor);
        var nome = FormatoCofreWindows.NomeValido(referencia);
        var conteudo = FormatoCofreWindows.Serializar(_protetor.Proteger(Encoding.UTF8.GetBytes(valor), FormatoCofreWindows.Entropia(nome)));
        var destino = FormatoCofreWindows.Caminho(_diretorio, nome);
        var temporario = Path.Combine(_diretorio, $"{nome}.{Guid.NewGuid():N}.tmp");

        await TravaEscrita.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(temporario, conteudo, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
            File.Move(temporario, destino, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporario))
            {
                File.Delete(temporario);
            }

            TravaEscrita.Release();
        }
    }
}
