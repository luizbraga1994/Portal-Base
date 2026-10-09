using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Catalogo;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Repositorios;

/// <summary>Repositório do agregado <see cref="Tenant"/> sobre o banco de catálogo.</summary>
public sealed class RepositorioTenant : IRepositorioTenant
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public RepositorioTenant(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async ValueTask<bool> ExisteComCnpjAsync(Cnpj cnpj, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cnpj);
        return await _contexto.Tenants
            .AnyAsync(tenant => tenant.Cnpj == cnpj, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> ExisteComNomeAsync(NomeTenant nome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nome);
        var valor = nome.Valor;
        return await _contexto.Tenants
            .AnyAsync(tenant => tenant.Nome == valor, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<Tenant?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await _contexto.Tenants
            .FirstOrDefaultAsync(tenant => tenant.Id == id, cancellationToken)
            .ConfigureAwait(false);

    public ValueTask AdicionarAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        _contexto.Tenants.Add(tenant);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<Guid>> ListarProvisionaveisAsync(CancellationToken cancellationToken = default)
        => await _contexto.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Estado != EstadoProvisionamento.Desabilitado)
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
