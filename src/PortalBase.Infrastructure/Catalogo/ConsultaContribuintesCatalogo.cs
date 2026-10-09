using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Catalogo;

/// <summary>
/// Lista de contribuintes do catálogo para a tela da plataforma. Consulta LINQ (sem SQL concatenado), somente leitura e
/// limitada; projeta só dados cadastrais públicos — nunca a base física, a referência de conexão ou o texto do erro.
/// A autorização (permissão de plataforma) é feita pelo caso de uso antes de chamar.
/// </summary>
public sealed class ConsultaContribuintesCatalogo : IConsultaContribuintesPlataforma
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public ConsultaContribuintesCatalogo(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<IReadOnlyList<ResumoContribuinte>> ListarAsync(int maximo, CancellationToken cancellationToken = default)
    {
        if (maximo < 1)
        {
            return [];
        }

        var tenants = await _contexto.Tenants.AsNoTracking()
            .OrderBy(t => t.Nome)
            .Take(maximo)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return tenants
            .Select(t => new ResumoContribuinte(
                t.Id, t.Nome, t.Cnpj.Valor, t.RazaoSocial, t.Estado, !string.IsNullOrWhiteSpace(t.UltimoErro), t.CriadoEmUtc, t.UltimaMigracao))
            .ToList();
    }
}
