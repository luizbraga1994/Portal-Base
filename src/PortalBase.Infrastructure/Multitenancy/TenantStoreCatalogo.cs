using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Multitenancy;

/// <summary>Lê metadados do tenant do CATÁLOGO. Nunca devolve credenciais (regra 7).</summary>
public sealed class TenantStoreCatalogo : ITenantStore
{
    private readonly PortalBaseCatalogoDbContext _catalogo;

    public TenantStoreCatalogo(PortalBaseCatalogoDbContext catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        _catalogo = catalogo;
    }

    public async ValueTask<TenantMetadados?> ObterAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var registro = await _catalogo.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.Id,
                t.NomeBaseFisica,
                t.ReferenciaSegredoConexao,
                t.Estado,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (registro is null)
        {
            return null;
        }

        return new TenantMetadados(
            registro.Id,
            registro.NomeBaseFisica,
            registro.ReferenciaSegredoConexao,
            Operacional: registro.Estado == EstadoProvisionamento.Ativo);
    }
}
