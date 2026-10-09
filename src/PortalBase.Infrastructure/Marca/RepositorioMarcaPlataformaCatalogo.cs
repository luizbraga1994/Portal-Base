using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Marca;
using PortalBase.Domain.Marca;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Marca;

/// <summary>
/// Repositório da logo GLOBAL do portal, no banco de CATÁLOGO (linha única). Sem credenciais (regra 7).
/// </summary>
public sealed class RepositorioMarcaPlataformaCatalogo : IRepositorioMarcaPlataforma
{
    private readonly PortalBaseCatalogoDbContext _catalogo;
    private readonly DbContextOptions<PortalBaseCatalogoDbContext> _opcoesCatalogo;

    public RepositorioMarcaPlataformaCatalogo(
        PortalBaseCatalogoDbContext catalogo, DbContextOptions<PortalBaseCatalogoDbContext> opcoesCatalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        ArgumentNullException.ThrowIfNull(opcoesCatalogo);
        _catalogo = catalogo;
        _opcoesCatalogo = opcoesCatalogo;
    }

    public Task<MarcaVisual?> ObterAsync(CancellationToken cancellationToken = default)
        => _catalogo.MarcaVisualPlataforma
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // P62-R1: o layout (LayoutEntrada/BarraSuperior) lê a logo enquanto a página, no mesmo request SSR, consulta o
    // catálogo — com o contexto compartilhado isso dava "A second operation was started on this context instance"
    // (erro 500 intermitente em /conta/selecionar). Leitura de exibição em contexto PRÓPRIO, sem rastreamento.
    public async Task<MarcaVisual?> ObterParaExibicaoAsync(CancellationToken cancellationToken = default)
    {
        await using var contexto = new PortalBaseCatalogoDbContext(_opcoesCatalogo);
        return await contexto.MarcaVisualPlataforma
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ResumoMarcaVisual?> ObterResumoAsync(CancellationToken cancellationToken = default)
    {
        await using var contexto = new PortalBaseCatalogoDbContext(_opcoesCatalogo);
        return await contexto.MarcaVisualPlataforma
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new ResumoMarcaVisual(x.ContentType, x.HashSha256, x.TamanhoBytes))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SalvarAsync(MarcaVisual marca, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marca);

        var jaRastreado = _catalogo.MarcaVisualPlataforma.Local.Any(x => x.Id == marca.Id);
        if (!jaRastreado)
        {
            var existe = await _catalogo.MarcaVisualPlataforma.AnyAsync(x => x.Id == marca.Id, cancellationToken).ConfigureAwait(false);
            if (existe)
            {
                _catalogo.MarcaVisualPlataforma.Update(marca);
            }
            else
            {
                await _catalogo.MarcaVisualPlataforma.AddAsync(marca, cancellationToken).ConfigureAwait(false);
            }
        }

        await _catalogo.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
