using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Marca;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Marca;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Persistencia.BaseTenant;

namespace PortalBase.Infrastructure.Marca;

/// <summary>
/// Repositório da logo do CONTRIBUINTE no BANCO DO TENANT (isolamento físico). Abre o contexto SOMENTE
/// pela fábrica fail-closed a partir do <see cref="ITenantContext"/> autorizado — nunca escolhe a base
/// livremente (regras 4-6). Há no máximo uma linha por base. Descartável.
/// </summary>
public sealed class RepositorioMarcaTenant : IRepositorioMarcaTenant, IDisposable, IAsyncDisposable
{
    private readonly IFabricaContextoTenant _fabrica;
    private readonly ITenantContext _tenant;
    private PortalBaseTenantDbContext? _contexto;

    public RepositorioMarcaTenant(IFabricaContextoTenant fabrica, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(fabrica);
        ArgumentNullException.ThrowIfNull(tenant);
        _fabrica = fabrica;
        _tenant = tenant;
    }

    public async Task<MarcaVisual?> ObterAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await ContextoAsync(cancellationToken).ConfigureAwait(false);
        return await contexto.MarcaVisual
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ResumoMarcaVisual?> ObterResumoAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await ContextoAsync(cancellationToken).ConfigureAwait(false);
        return await contexto.MarcaVisual
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new ResumoMarcaVisual(x.ContentType, x.HashSha256, x.TamanhoBytes))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SalvarAsync(MarcaVisual marca, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marca);
        var contexto = await ContextoAsync(cancellationToken).ConfigureAwait(false);

        var jaRastreado = contexto.MarcaVisual.Local.Any(x => x.Id == marca.Id);
        if (!jaRastreado)
        {
            var existe = await contexto.MarcaVisual.AnyAsync(x => x.Id == marca.Id, cancellationToken).ConfigureAwait(false);
            if (existe)
            {
                contexto.MarcaVisual.Update(marca);
            }
            else
            {
                await contexto.MarcaVisual.AddAsync(marca, cancellationToken).ConfigureAwait(false);
            }
        }

        await contexto.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _contexto?.Dispose();
        _contexto = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_contexto is not null)
        {
            await _contexto.DisposeAsync().ConfigureAwait(false);
            _contexto = null;
        }
    }

    private async ValueTask<PortalBaseTenantDbContext> ContextoAsync(CancellationToken cancellationToken)
        => _contexto ??= await _fabrica.CriarAsync(_tenant, cancellationToken).ConfigureAwait(false);
}
