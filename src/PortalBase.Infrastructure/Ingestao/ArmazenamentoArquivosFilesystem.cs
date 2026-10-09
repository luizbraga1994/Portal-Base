using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Seguranca;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Ingestao;

/// <summary>
/// Armazenamento de arquivos em DIRETÓRIO PRIVADO no filesystem (Prompt 08, seções 13, 15, 26).
/// Nunca escreve em wwwroot. Cada segmento do caminho relativo é VALIDADO contra path traversal
/// (<see cref="GuardaCaminho"/>) e o caminho final é garantido DENTRO da raiz. O nome físico é
/// gerado pelo servidor (nunca o do usuário). Streaming — não carrega o arquivo em memória.
/// </summary>
public sealed class ArmazenamentoArquivosFilesystem : IArmazenamentoArquivos
{
    private readonly string _raiz;

    public ArmazenamentoArquivosFilesystem(IOptions<OpcoesArmazenamento> opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        var configurado = opcoes.Value.DiretorioRaiz;
        var raiz = string.IsNullOrWhiteSpace(configurado)
            ? Path.Combine(Path.GetTempPath(), "portalbase-uploads")
            : configurado;

        _raiz = Path.GetFullPath(raiz);
        Directory.CreateDirectory(_raiz);
    }

    public async ValueTask<string> SalvarAsync(string caminhoRelativo, Stream conteudo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conteudo);
        var (completo, normalizado) = Resolver(caminhoRelativo);

        var diretorio = Path.GetDirectoryName(completo);
        if (!string.IsNullOrEmpty(diretorio))
        {
            Directory.CreateDirectory(diretorio);
        }

        // FileMode.CreateNew: nunca sobrescreve um arquivo existente (nome é único por Guid).
        await using (var destino = new FileStream(
            completo, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
        {
            await conteudo.CopyToAsync(destino, cancellationToken).ConfigureAwait(false);
            await destino.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return normalizado;
    }

    public ValueTask<Stream> AbrirLeituraAsync(string referencia, CancellationToken cancellationToken = default)
    {
        var (completo, _) = Resolver(referencia);
        if (!File.Exists(completo))
        {
            throw new EntradaInvalidaException("Referência de arquivo inexistente.");
        }

        Stream leitura = new FileStream(
            completo, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return ValueTask.FromResult(leitura);
    }

    public ValueTask RemoverAsync(string referencia, CancellationToken cancellationToken = default)
    {
        var (completo, _) = Resolver(referencia);
        if (File.Exists(completo))
        {
            File.Delete(completo);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Valida cada segmento e garante que o caminho fique DENTRO da raiz. Retorna (absoluto, relativo normalizado).</summary>
    private (string Completo, string Normalizado) Resolver(string caminhoRelativo)
    {
        if (string.IsNullOrWhiteSpace(caminhoRelativo))
        {
            throw new EntradaInvalidaException("Caminho de armazenamento inválido.");
        }

        // Rejeita cedo caminhos absolutos, traversal ou iniciados por separador (defesa em camadas;
        // a validação por segmento é a barreira final). O caso de uso só gera caminhos relativos seguros.
        if (Path.IsPathRooted(caminhoRelativo)
            || caminhoRelativo.Contains("..", StringComparison.Ordinal)
            || caminhoRelativo.StartsWith('/')
            || caminhoRelativo.StartsWith('\\'))
        {
            throw new EntradaInvalidaException("Caminho de armazenamento inválido.");
        }

        var segmentos = caminhoRelativo.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segmentos.Length == 0)
        {
            throw new EntradaInvalidaException("Caminho de armazenamento inválido.");
        }

        var atual = _raiz;
        foreach (var segmento in segmentos)
        {
            // CombinarDentroDe valida o nome (sem traversal/separador/raiz) e confere a contenção.
            atual = GuardaCaminho.CombinarDentroDe(atual, segmento);
        }

        return (atual, string.Join('/', segmentos));
    }
}
