using Microsoft.Extensions.Options;
using PortalBase.Application.Ingestao;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Ingestao;

/// <summary>
/// Área de trabalho TEMPORÁRIA privada (Prompt 08, seções 13, 34). Cria um arquivo temporário
/// SEEKABLE com nome aleatório, fora de wwwroot, marcado para EXCLUSÃO ao fechar
/// (<see cref="FileOptions.DeleteOnClose"/>): o caso de uso faz hash, validação e varredura em
/// passes sem carregar o arquivo em memória. O stream é descartado (e o arquivo apagado) pelo
/// <c>await using</c> do chamador — inclusive em falha.
/// </summary>
public sealed class AreaTrabalhoTemporaria : IAreaTrabalhoTemporaria
{
    private readonly string _diretorio;

    public AreaTrabalhoTemporaria(IOptions<OpcoesArmazenamento> opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        var configurado = opcoes.Value.DiretorioTemporario;
        _diretorio = string.IsNullOrWhiteSpace(configurado)
            ? Path.Combine(Path.GetTempPath(), "portalbase-tmp")
            : Path.GetFullPath(configurado);

        Directory.CreateDirectory(_diretorio);
    }

    public Task<Stream> CriarAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var caminho = Path.Combine(_diretorio, $"{Guid.NewGuid():N}.tmp");

        // DeleteOnClose: o arquivo é removido quando o stream é descartado (não deixa resíduo).
        Stream stream = new FileStream(
            caminho,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        return Task.FromResult(stream);
    }
}
