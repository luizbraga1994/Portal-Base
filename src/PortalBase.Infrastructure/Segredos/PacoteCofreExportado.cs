using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PortalBase.Application.Abstracoes;

namespace PortalBase.Infrastructure.Segredos;

/// <summary>
/// PACOTE EXPORTADO DO COFRE LOCAL (BKP-001, auditoria de 08/10/2026). O cofre do Windows é cifrado com o DPAPI da
/// máquina: se o servidor se perde, os arquivos <c>.segredo</c> não abrem em outro lugar — e sem a chave dos backups
/// nenhum backup decifra. O <c>scripts/cofre-windows.ps1 -Acao Exportar</c> gera este pacote, protegido por uma SENHA
/// (guardada fora do servidor e longe dos backups); o <c>-Acao Restaurar</c> o regrava no cofre de outra máquina e o
/// comando <c>decifrar-backup</c> o usa para decifrar um backup em qualquer máquina.
///
/// Formato v1 (o script grava, este código lê):
/// <code>
/// [0..10)   "COFREPKG01" (ASCII)
/// [10..14)  iterações do PBKDF2 (int32 big-endian, 100 mil a 10 milhões)
/// [14..30)  sal (16 bytes)
/// [30..46)  IV (16 bytes)
/// [46..n-32) texto cifrado: AES-256-CBC, PKCS7
/// [n-32..n) HMAC-SHA256 de tudo o que vem antes (cifra-depois-autentica)
/// chaves = PBKDF2-HMAC-SHA256(senha UTF-8, sal, iterações, 64 bytes): cifra = [0..32), HMAC = [32..64)
/// texto claro (UTF-8, linhas com \n): "COFRE v1" e depois "&lt;nome&gt;:&lt;Base64 do valor&gt;"
/// </code>
/// O HMAC é conferido (tempo constante) ANTES de decifrar: senha errada ou arquivo alterado falham sem revelar nada.
/// Nenhum valor é logado; as mensagens só dizem o que fazer.
/// </summary>
public static class PacoteCofreExportado
{
    public const string Extensao = ".portalbasecofre";

    private static readonly byte[] Magico = "COFREPKG01"u8.ToArray();
    private const string CabecalhoTexto = "COFRE v1";
    private const int TamanhoSal = 16;
    private const int TamanhoIv = 16;
    private const int TamanhoMac = 32;
    private const int IteracoesMinimas = 100_000;
    private const int IteracoesMaximas = 10_000_000;
    private const int TamanhoMaximo = 64 * 1024 * 1024;
    private static readonly int InicioCifrado = Magico.Length + 4 + TamanhoSal + TamanhoIv;

    /// <summary>Abre o pacote com a senha e devolve os segredos (nome → valor). Falha sem detalhe se algo não confere.</summary>
    public static IReadOnlyDictionary<string, string> Abrir(byte[] pacote, string senha)
    {
        ArgumentNullException.ThrowIfNull(pacote);
        ArgumentException.ThrowIfNullOrEmpty(senha);
        if (pacote.Length > TamanhoMaximo
            || pacote.Length < InicioCifrado + 16 + TamanhoMac
            || !pacote.AsSpan(0, Magico.Length).SequenceEqual(Magico))
        {
            throw new InvalidDataException("O arquivo não é um pacote do cofre do Portal Base (.portalbasecofre v1).");
        }

        var iteracoes = BinaryPrimitives.ReadInt32BigEndian(pacote.AsSpan(Magico.Length, 4));
        var cifradoTamanho = pacote.Length - InicioCifrado - TamanhoMac;
        if (iteracoes is < IteracoesMinimas or > IteracoesMaximas || cifradoTamanho % 16 != 0)
        {
            throw new InvalidDataException("Pacote do cofre malformado.");
        }

        var sal = pacote.AsSpan(Magico.Length + 4, TamanhoSal).ToArray();
        var iv = pacote.AsSpan(Magico.Length + 4 + TamanhoSal, TamanhoIv).ToArray();
        var chaves = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(senha), sal, iteracoes, HashAlgorithmName.SHA256, 64);
        byte[]? claro = null;
        try
        {
            var esperado = HMACSHA256.HashData(chaves.AsSpan(32, 32), pacote.AsSpan(0, pacote.Length - TamanhoMac));
            if (!CryptographicOperations.FixedTimeEquals(esperado, pacote.AsSpan(pacote.Length - TamanhoMac)))
            {
                throw new InvalidDataException("Senha do pacote incorreta ou pacote alterado.");
            }

            using var aes = Aes.Create();
            aes.Key = chaves.AsSpan(0, 32).ToArray();
            claro = aes.DecryptCbc(pacote.AsSpan(InicioCifrado, cifradoTamanho), iv, PaddingMode.PKCS7);
            return Ler(Encoding.UTF8.GetString(claro));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chaves);
            if (claro is not null)
            {
                CryptographicOperations.ZeroMemory(claro);
            }
        }
    }

    private static Dictionary<string, string> Ler(string texto)
    {
        var linhas = texto.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (linhas.Length == 0 || !string.Equals(linhas[0], CabecalhoTexto, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Pacote do cofre com conteúdo desconhecido.");
        }

        var segredos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var linha in linhas.Skip(1))
        {
            var separador = linha.IndexOf(':', StringComparison.Ordinal);
            if (separador <= 0)
            {
                throw new InvalidDataException("Pacote do cofre com conteúdo desconhecido.");
            }

            var nome = FormatoCofreWindows.NomeValido(linha[..separador]);
            try
            {
                segredos[nome] = Encoding.UTF8.GetString(Convert.FromBase64String(linha[(separador + 1)..]));
            }
            catch (FormatException)
            {
                throw new InvalidDataException("Pacote do cofre com conteúdo desconhecido.");
            }
        }

        return segredos;
    }
}

/// <summary>Provedor de segredos (só leitura) a partir de um pacote exportado já aberto — usado pelo decifrar-backup.</summary>
public sealed class ProvedorSegredosPacote : IProvedorSegredos
{
    private readonly IReadOnlyDictionary<string, string> _segredos;

    public ProvedorSegredosPacote(IReadOnlyDictionary<string, string> segredos)
    {
        ArgumentNullException.ThrowIfNull(segredos);
        _segredos = segredos;
    }

    public ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default)
    {
        var nome = FormatoCofreWindows.NomeValido(referencia);
        return _segredos.TryGetValue(nome, out var valor)
            ? ValueTask.FromResult(valor)
            : throw new InvalidOperationException($"Segredo '{nome}' não está no pacote do cofre.");
    }
}
