using System.Security.Cryptography;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Backup;

namespace PortalBase.Infrastructure.Backup;

/// <summary>O que há dentro de um artefato de backup decifrado (para o operador saber como restaurar).</summary>
public enum ConteudoArtefatoBackup
{
    Desconhecido = 0,

    /// <summary>Dump do MySQL (catálogo ou base de um contribuinte): restaurar com <c>mysql</c>.</summary>
    DumpSql = 1,

    /// <summary>Pacote ZIP com os arquivos (<c>arquivos/</c>) de um contribuinte.</summary>
    PacoteZip = 2,
}

/// <summary>Resultado da decifração: hash do artefato (para conferir com o registrado), tamanho e tipo do conteúdo.</summary>
public sealed record ResultadoDecifracaoBackup(string HashArtefatoSha256, long BytesDecifrados, ConteudoArtefatoBackup Conteudo);

/// <summary>
/// Decifra um artefato de backup FORA do portal — recuperação de desastre (BKP-001, auditoria de 08/10/2026): o
/// servidor ou o catálogo se perderam e é preciso abrir um <c>.bak.enc</c> com a chave dos backups (do cofre do servidor,
/// de uma pasta de cofre restaurada ou do pacote exportado do cofre). Não usa banco. Regras:
/// <list type="bullet">
/// <item>Nunca sobrescreve: a saída não pode existir.</item>
/// <item>Nunca deixa saída parcial: grava num temporário ao lado e só dá o nome final depois de decifrar TUDO (o GCM
/// confere cada bloco; um bloco adulterado no meio apaga o que já tinha sido escrito).</item>
/// <item>Mostra o SHA-256 do artefato, para conferir com o hash registrado em Operações → Backups.</item>
/// </list>
/// </summary>
public static class DecifradorArtefatoBackup
{
    public static async Task<ResultadoDecifracaoBackup> DecifrarAsync(
        IProvedorSegredos segredos,
        string chaveRef,
        string arquivoArtefato,
        string arquivoSaida,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentException.ThrowIfNullOrWhiteSpace(chaveRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(arquivoArtefato);
        ArgumentException.ThrowIfNullOrWhiteSpace(arquivoSaida);

        var origem = Path.GetFullPath(arquivoArtefato);
        var destino = Path.GetFullPath(arquivoSaida);
        if (!File.Exists(origem))
        {
            throw new FileNotFoundException("O artefato de backup informado não existe.");
        }

        if (File.Exists(destino) || Directory.Exists(destino))
        {
            throw new IOException("O arquivo de saída já existe; escolha outro nome (nada é sobrescrito).");
        }

        if (string.Equals(origem, destino, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A saída não pode ser o próprio artefato.");
        }

        string hash;
        await using (var leitura = Abrir(origem))
        {
            hash = await HashBackup.CalcularAsync(leitura, cancellationToken).ConfigureAwait(false);
        }

        var pasta = Path.GetDirectoryName(destino)!;
        Directory.CreateDirectory(pasta);
        var temporario = Path.Combine(pasta, $".{Path.GetFileName(destino)}.{Guid.NewGuid():N}.parcial");
        var cripto = new CriptografiaBackupAesGcm(segredos, new OpcoesBackup { ChaveCriptografiaRef = chaveRef.Trim() });
        try
        {
            long bytes;
            await using (var leitura = Abrir(origem))
            await using (var escrita = new FileStream(temporario, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                try
                {
                    await cripto.DecriptografarAsync(leitura, escrita, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is CryptographicException or AuthenticationTagMismatchException)
                {
                    throw new InvalidDataException(
                        "Não foi possível decifrar: a chave não é a usada neste backup, o artefato foi alterado/truncado ou o arquivo não é um backup cifrado.");
                }

                await escrita.FlushAsync(cancellationToken).ConfigureAwait(false);
                bytes = escrita.Length;
            }

            var conteudo = await DetectarAsync(temporario, cancellationToken).ConfigureAwait(false);
            File.Move(temporario, destino, overwrite: false);
            return new ResultadoDecifracaoBackup(hash, bytes, conteudo);
        }
        finally
        {
            if (File.Exists(temporario))
            {
                File.Delete(temporario);
            }
        }
    }

    private static FileStream Abrir(string caminho)
        => new(caminho, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    private static async Task<ConteudoArtefatoBackup> DetectarAsync(string caminho, CancellationToken cancellationToken)
    {
        var inicio = new byte[64];
        int lidos;
        await using (var leitura = Abrir(caminho))
        {
            lidos = await leitura.ReadAtLeastAsync(inicio, inicio.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        }

        // ZIP: "PK\x03\x04" (com arquivos) ou "PK\x05\x06" (pacote vazio).
        if (lidos >= 4 && inicio[0] == (byte)'P' && inicio[1] == (byte)'K' && (inicio[2], inicio[3]) is (3, 4) or (5, 6))
        {
            return ConteudoArtefatoBackup.PacoteZip;
        }

        // Dump do mysqldump: depois de espaços/quebras iniciais (o real começa com "\n/*!40101 SET ..."), um comentário
        // ("--" ou "/*") ou um comando SQL.
        var texto = System.Text.Encoding.ASCII.GetString(inicio, 0, lidos).TrimStart(' ', '\t', '\r', '\n');
        string[] inicios = ["--", "/*", "SET ", "CREATE ", "DROP ", "LOCK ", "INSERT "];
        return inicios.Any(i => texto.StartsWith(i, StringComparison.OrdinalIgnoreCase))
            ? ConteudoArtefatoBackup.DumpSql
            : ConteudoArtefatoBackup.Desconhecido;
    }
}
