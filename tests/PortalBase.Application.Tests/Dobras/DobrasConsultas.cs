using PortalBase.Application.Consultas;
using PortalBase.Application.Incidentes;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Application.Tests.Dobras;

// Fakes das portas de read-model (Prompt 36): capturam o filtro efetivamente recebido (para asserir
// normalização/whitelisting) e devolvem uma página configurável. Nunca tocam banco.

internal sealed class ConsultaArquivosImportacaoFake : IConsultaArquivosImportacao
{
    public FiltroArquivosImportacao? UltimoFiltro { get; private set; }

    public Task<ResultadoPaginado<ItemArquivoImportacao>> ConsultarAsync(FiltroArquivosImportacao filtro, CancellationToken cancellationToken = default)
    {
        UltimoFiltro = filtro;
        return Task.FromResult(ResultadoPaginado.Vazio<ItemArquivoImportacao>(1, 50));
    }
}

internal sealed class ConsultaIncidentesFake : IConsultaIncidentes
{
    public FiltroIncidentes? UltimoFiltro { get; private set; }

    public Task<ResultadoPaginado<ItemIncidente>> ConsultarAsync(FiltroIncidentes filtro, CancellationToken cancellationToken = default)
    {
        UltimoFiltro = filtro;
        return Task.FromResult(ResultadoPaginado.Vazio<ItemIncidente>(1, 50));
    }
}

internal sealed class RegistroSaudeWorkersFake : IRegistroSaudeWorkers
{
    public List<EstadoSaudeWorker> Estados { get; } = [];

    public void RegistrarInicio(string worker, DateTimeOffset agoraUtc) { }

    public void RegistrarBatida(string worker, DateTimeOffset agoraUtc) { }

    public void RegistrarSucesso(string worker, long itensProcessados, DateTimeOffset agoraUtc) { }

    public void RegistrarErro(string worker, string codigoErro, DateTimeOffset agoraUtc) { }

    public void RegistrarEncerramento(string worker, DateTimeOffset agoraUtc) { }

    public EstadoSaudeWorker? Obter(string worker) => Estados.Find(e => e.Worker == worker);

    public IReadOnlyList<EstadoSaudeWorker> Todos() => Estados;
}

internal sealed class RepositorioSaudeWorkersPersistidaFake : IRepositorioSaudeWorkersPersistida
{
    public List<EstadoSaudeWorker> Estados { get; } = [];

    public Task GravarAsync(IReadOnlyList<EstadoSaudeWorker> estados, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
    {
        Estados.RemoveAll(e => estados.Any(n => n.Worker == e.Worker));
        Estados.AddRange(estados);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EstadoSaudeWorker>> ListarAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EstadoSaudeWorker>>(Estados.ToList());
}

internal sealed class RegistroUltimaIntegracaoFake : IRegistroUltimaIntegracao
{
    public List<EstadoUltimaIntegracao> Estados { get; } = [];

    public void RegistrarSucesso(string integracao, DateTimeOffset agoraUtc) { }

    public void RegistrarFalha(string integracao, string codigoErro, DateTimeOffset agoraUtc) { }

    public EstadoUltimaIntegracao? Obter(string integracao) => Estados.Find(e => e.Integracao == integracao);

    public IReadOnlyList<EstadoUltimaIntegracao> Todos() => Estados;
}
