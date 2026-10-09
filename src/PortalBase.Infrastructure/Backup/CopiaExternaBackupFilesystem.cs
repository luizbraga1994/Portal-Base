using Microsoft.Extensions.Options;
using PortalBase.Application.Backup;
using PortalBase.Application.Seguranca;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// CÓPIA EXTERNA (offsite) dos artefatos de backup num diretório de OUTRO disco ou servidor
/// (<c>ArmazenamentoBackup:DiretorioOffsite</c>, ex.: <c>E:\Copia</c> ou <c>\\servidor\PortalBase</c>) — BKP-003. A cópia
/// é gravada com nome temporário, CONFERIDA pelo SHA-256 e só então recebe o nome definitivo: nunca fica no destino um
/// arquivo divergente com cara de backup bom. Mesma referência lógica do primário (segmentos validados contra path
/// traversal). O artefato já vem cifrado: o destino externo nunca vê dado em claro nem a chave.
/// </summary>
public sealed class CopiaExternaBackupFilesystem : ICopiaExternaBackup
{
    private readonly string? _raiz;

    public CopiaExternaBackupFilesystem(IOptions<OpcoesArmazenamentoBackup> opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        var configurado = opcoes.Value.DiretorioOffsite;
        _raiz = string.IsNullOrWhiteSpace(configurado) ? null : Path.GetFullPath(configurado.Trim());
    }

    public bool Configurada => _raiz is not null;

    public async Task CopiarAsync(string referenciaLogica, Stream artefato, string hashSha256Esperado, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artefato);
        ArgumentException.ThrowIfNullOrWhiteSpace(hashSha256Esperado);
        var destino = Resolver(referenciaLogica);

        var diretorio = Path.GetDirectoryName(destino)!;
        Directory.CreateDirectory(diretorio);
        var temporario = Path.Combine(diretorio, $".{Guid.NewGuid():N}.parcial");
        try
        {
            await using (var arquivo = new FileStream(temporario, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await artefato.CopyToAsync(arquivo, cancellationToken).ConfigureAwait(false);
                await arquivo.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Confere o que FOI GRAVADO no destino (relido do disco), não o que foi enviado.
            string hashGravado;
            await using (var leitura = new FileStream(temporario, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                hashGravado = await HashBackup.CalcularAsync(leitura, cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(hashGravado, hashSha256Esperado, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A cópia externa do backup não confere com o hash do artefato.");
            }

            File.Move(temporario, destino, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporario))
            {
                File.Delete(temporario);
            }
        }
    }

    public Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default)
    {
        if (_raiz is null)
        {
            return Task.CompletedTask;
        }

        var caminho = Resolver(referenciaLogica);
        if (File.Exists(caminho))
        {
            File.Delete(caminho);
        }

        return Task.CompletedTask;
    }

    private string Resolver(string referenciaLogica)
    {
        if (_raiz is null)
        {
            throw new InvalidOperationException("Cópia externa de backup não configurada (ArmazenamentoBackup:DiretorioOffsite).");
        }

        if (string.IsNullOrWhiteSpace(referenciaLogica)
            || Path.IsPathRooted(referenciaLogica)
            || referenciaLogica.Contains("..", StringComparison.Ordinal))
        {
            throw new EntradaInvalidaException("Referência de backup inválida.");
        }

        var segmentos = referenciaLogica.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segmentos.Length == 0)
        {
            throw new EntradaInvalidaException("Referência de backup inválida.");
        }

        var atual = _raiz;
        foreach (var segmento in segmentos)
        {
            atual = GuardaCaminho.CombinarDentroDe(atual, segmento);
        }

        return atual;
    }
}
