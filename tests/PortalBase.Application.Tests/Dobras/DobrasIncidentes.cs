using PortalBase.Application.Incidentes;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Application.Tests.Dobras;

/// <summary>Repositório de incidentes de segurança em memória (Prompt 30).</summary>
internal sealed class RepositorioIncidentesFake : IRepositorioIncidentes
{
    public List<IncidenteSeguranca> Itens { get; } = [];

    public int Salvamentos { get; private set; }

    public Task AdicionarAsync(IncidenteSeguranca incidente, CancellationToken cancellationToken = default)
    {
        Itens.Add(incidente);
        return Task.CompletedTask;
    }

    public Task<IncidenteSeguranca?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Itens.Find(x => x.Id == id));

    public Task<IncidenteSeguranca?> ObterPorCodigoAsync(string codigo, CancellationToken cancellationToken = default)
        => Task.FromResult(Itens.Find(x => x.Codigo == codigo));

    public Task<IReadOnlyList<IncidenteSeguranca>> ObterRecentesAsync(int limite, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<IncidenteSeguranca>>(Itens.OrderByDescending(x => x.DetectadoEmUtc).Take(Math.Max(1, limite)).ToList());

    public Task SalvarAsync(CancellationToken cancellationToken = default)
    {
        Salvamentos++;
        return Task.CompletedTask;
    }
}
