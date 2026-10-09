using Microsoft.Extensions.Options;
using PortalBase.Application.Backup;
using PortalBase.Application.Seguranca;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// STORE de artefatos de backup em FILESYSTEM (Prompt 50, Fases 6/25). A raiz deve ficar em um failure
/// domain separado do banco (config). Cada segmento da referência lógica é VALIDADO contra path traversal
/// (<see cref="GuardaCaminho"/>) e contido DENTRO da raiz — a referência é uma CHAVE lógica, jamais um
/// caminho com credencial. O conteúdo já vem CIFRADO da camada de aplicação (a chave nunca acompanha o
/// artefato). Streaming — não carrega o arquivo inteiro em memória.
/// </summary>
public sealed class ArmazenamentoBackupFilesystem : IArmazenamentoBackup
{
    private readonly string _raiz;

    public ArmazenamentoBackupFilesystem(IOptions<OpcoesArmazenamentoBackup> opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        var configurado = opcoes.Value.DiretorioRaiz;
        var raiz = string.IsNullOrWhiteSpace(configurado)
            ? Path.Combine(Path.GetTempPath(), "portalbase-backups")
            : configurado;

        _raiz = Path.GetFullPath(raiz);
        Directory.CreateDirectory(_raiz);
    }

    public async Task<string> GravarAsync(string referenciaLogica, Stream conteudo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conteudo);
        var (completo, normalizado) = Resolver(referenciaLogica);

        var diretorio = Path.GetDirectoryName(completo);
        if (!string.IsNullOrEmpty(diretorio))
        {
            Directory.CreateDirectory(diretorio);
        }

        await using (var destino = new FileStream(
            completo, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
        {
            await conteudo.CopyToAsync(destino, cancellationToken).ConfigureAwait(false);
            await destino.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return normalizado;
    }

    public Task<Stream> AbrirAsync(string referenciaLogica, CancellationToken cancellationToken = default)
    {
        var (completo, _) = Resolver(referenciaLogica);
        if (!File.Exists(completo))
        {
            throw new EntradaInvalidaException("Artefato de backup inexistente.");
        }

        Stream leitura = new FileStream(
            completo, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return Task.FromResult(leitura);
    }

    public Task<bool> ExisteAsync(string referenciaLogica, CancellationToken cancellationToken = default)
    {
        var (completo, _) = Resolver(referenciaLogica);
        return Task.FromResult(File.Exists(completo));
    }

    public Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default)
    {
        var (completo, _) = Resolver(referenciaLogica);
        if (File.Exists(completo))
        {
            File.Delete(completo);
        }

        return Task.CompletedTask;
    }

    private (string Completo, string Normalizado) Resolver(string referenciaLogica)
    {
        if (string.IsNullOrWhiteSpace(referenciaLogica)
            || Path.IsPathRooted(referenciaLogica)
            || referenciaLogica.Contains("..", StringComparison.Ordinal)
            || referenciaLogica.StartsWith('/')
            || referenciaLogica.StartsWith('\\'))
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

        return (atual, string.Join('/', segmentos));
    }
}
