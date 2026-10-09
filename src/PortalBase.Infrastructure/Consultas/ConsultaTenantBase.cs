using PortalBase.Application.Multitenancy;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Persistencia.BaseTenant;

namespace PortalBase.Infrastructure.Consultas;

/// <summary>
/// Base dos READ-MODELS do TENANT (Prompt 36). Obtém o contexto de leitura APENAS pela fábrica
/// fail-closed a partir do <see cref="ITenantContext"/> autorizado (regras 4, 5, 6) — nunca escolhe a
/// base livremente. O isolamento é físico (banco por contribuinte). Somente leitura: as consultas usam
/// <c>AsNoTracking</c> e projeções; nada é rastreado nem salvo. Descartável.
/// </summary>
public abstract class ConsultaTenantBase : IDisposable, IAsyncDisposable
{
    private readonly IFabricaContextoTenant _fabrica;
    private readonly ITenantContext _tenant;
    private PortalBaseTenantDbContext? _contexto;

    protected ConsultaTenantBase(IFabricaContextoTenant fabrica, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(fabrica);
        ArgumentNullException.ThrowIfNull(tenant);
        _fabrica = fabrica;
        _tenant = tenant;
    }

    protected async ValueTask<PortalBaseTenantDbContext> ContextoAsync(CancellationToken cancellationToken)
        => _contexto ??= await _fabrica.CriarAsync(_tenant, cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        _contexto?.Dispose();
        _contexto = null;
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_contexto is not null)
        {
            await _contexto.DisposeAsync().ConfigureAwait(false);
            _contexto = null;
        }

        GC.SuppressFinalize(this);
    }
}
