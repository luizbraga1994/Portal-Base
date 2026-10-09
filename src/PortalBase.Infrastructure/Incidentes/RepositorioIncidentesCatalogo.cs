using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Incidentes;
using PortalBase.Domain.Incidentes;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Incidentes;

/// <summary>
/// Repositório de INCIDENTES DE SEGURANÇA no banco de CATÁLOGO (Prompt 30, seções 45-47) — governança de
/// plataforma, append-only. Carrega a TIMELINE junto (evidência forense). O acesso é sempre precedido de
/// autorização de plataforma no serviço (deny-by-default).
/// </summary>
public sealed class RepositorioIncidentesCatalogo : IRepositorioIncidentes
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public RepositorioIncidentesCatalogo(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task AdicionarAsync(IncidenteSeguranca incidente, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incidente);
        await _contexto.IncidentesSeguranca.AddAsync(incidente, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IncidenteSeguranca?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await _contexto.IncidentesSeguranca
            .Include(x => x.Timeline)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IncidenteSeguranca?> ObterPorCodigoAsync(string codigo, CancellationToken cancellationToken = default)
        => await _contexto.IncidentesSeguranca
            .Include(x => x.Timeline)
            .FirstOrDefaultAsync(x => x.Codigo == codigo, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<IncidenteSeguranca>> ObterRecentesAsync(int limite, CancellationToken cancellationToken = default)
    {
        var teto = Math.Clamp(limite, 1, 500);
        return await _contexto.IncidentesSeguranca
            .Include(x => x.Timeline)
            .OrderByDescending(x => x.DetectadoEmUtc)
            .ThenBy(x => x.Id)
            .Take(teto)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SalvarAsync(CancellationToken cancellationToken = default)
        => await _contexto.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
}
