using System.IO.Compression;
using Microsoft.Extensions.Options;
using PortalBase.Application.Backup;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// Empacota num ZIP os ARQUIVOS de UM contribuinte para o backup do tipo Storage (BKP-005): os arquivos enviados
/// (<c>&lt;Armazenamento:DiretorioRaiz&gt;/&lt;tenant&gt;/</c>, entradas <c>arquivos/…</c>). Só essa pasta — nunca o
/// cofre, a pasta de outro contribuinte ou caminho fora da raiz. Se o seu portal guardar outros arquivos do
/// contribuinte em outra pasta, acrescente-a aqui com um prefixo próprio. Em streaming (arquivo por arquivo); o ZIP vazio também
/// é válido (contribuinte sem arquivos). Restauração: decifrar o artefato e extrair nas mesmas pastas (runbook).
/// </summary>
public sealed class EmpacotadorArquivosTenantZip : IEmpacotadorArquivosTenant
{
    private readonly string _raiz;

    public EmpacotadorArquivosTenantZip(IOptions<OpcoesArmazenamento> opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        var configurado = opcoes.Value.DiretorioRaiz;
        _raiz = Path.GetFullPath(string.IsNullOrWhiteSpace(configurado)
            ? Path.Combine(Path.GetTempPath(), "portalbase-uploads")
            : configurado);
    }

    public async Task EmpacotarAsync(Guid tenantId, Stream destino, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destino);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Contribuinte inválido.", nameof(tenantId));
        }

        var pasta = tenantId.ToString("N");
        using var zip = new ZipArchive(destino, ZipArchiveMode.Create, leaveOpen: true);
        await IncluirAsync(zip, Path.Combine(_raiz, pasta), "arquivos", cancellationToken).ConfigureAwait(false);
    }

    private static async Task IncluirAsync(ZipArchive zip, string origem, string prefixo, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(origem))
        {
            return;
        }

        var raizOrigem = Path.GetFullPath(origem) + Path.DirectorySeparatorChar;
        // Links simbólicos e junções são pulados (não segue nada para fora da pasta do contribuinte).
        var opcoes = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var arquivo in Directory.EnumerateFiles(origem, "*", opcoes).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completo = Path.GetFullPath(arquivo);
            if (!completo.StartsWith(raizOrigem, StringComparison.Ordinal))
            {
                continue; // link/junção para fora da pasta do contribuinte: nunca entra no pacote
            }

            var relativo = Path.GetRelativePath(origem, completo).Replace('\\', '/');
            var entrada = zip.CreateEntry($"{prefixo}/{relativo}", CompressionLevel.Optimal);
            entrada.LastWriteTime = File.GetLastWriteTimeUtc(completo);
            await using var leitura = new FileStream(completo, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var escrita = entrada.Open();
            await leitura.CopyToAsync(escrita, cancellationToken).ConfigureAwait(false);
        }
    }
}
