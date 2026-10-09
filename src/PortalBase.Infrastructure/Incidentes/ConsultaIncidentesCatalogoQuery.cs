using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Consultas;
using PortalBase.Application.Incidentes;
using PortalBase.Domain.Incidentes;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Incidentes;

/// <summary>
/// Read-model de INCIDENTES sobre o CATÁLOGO (Prompt 36, §27) — projeção paginada, AsNoTracking. NÃO usa a
/// fábrica de tenant: incidentes são governança de plataforma (cross-tenant). Nunca projeta evidências.
/// </summary>
public sealed class ConsultaIncidentesCatalogoQuery : IConsultaIncidentes
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public ConsultaIncidentesCatalogoQuery(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<ResultadoPaginado<ItemIncidente>> ConsultarAsync(FiltroIncidentes filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var f = filtro.Normalizado();

        var consulta = _contexto.IncidentesSeguranca.AsNoTracking();

        if (f.Tipo is TipoIncidente tipo)
        {
            consulta = consulta.Where(x => x.Tipo == tipo);
        }

        if (f.Severidade is SeveridadeIncidente severidade)
        {
            consulta = consulta.Where(x => x.Severidade == severidade);
        }

        if (f.Status is StatusIncidente status)
        {
            consulta = consulta.Where(x => x.Status == status);
        }

        if (f.TenantAfetadoId is Guid tenant)
        {
            consulta = consulta.Where(x => x.TenantAfetadoId == tenant);
        }

        if (!string.IsNullOrWhiteSpace(f.Codigo))
        {
            var codigo = f.Codigo.Trim();
            consulta = consulta.Where(x => x.Codigo == codigo);
        }

        // Período (Prompt 81) aplicado NO BANCO, antes da contagem, da ordenação e do Skip/Take.
        if (f.Desde is { } desde)
        {
            consulta = consulta.Where(x => x.DetectadoEmUtc >= desde);
        }

        if (f.Ate is { } ate)
        {
            consulta = consulta.Where(x => x.DetectadoEmUtc <= ate);
        }

        var total = await consulta.LongCountAsync(cancellationToken).ConfigureAwait(false);

        var asc = f.Direcao == DirecaoOrdenacao.Ascendente;
        consulta = f.Ordenacao switch
        {
            CampoOrdenacaoIncidente.Severidade => asc
                ? consulta.OrderBy(x => x.Severidade).ThenBy(x => x.Id)
                : consulta.OrderByDescending(x => x.Severidade).ThenBy(x => x.Id),
            CampoOrdenacaoIncidente.Status => asc
                ? consulta.OrderBy(x => x.Status).ThenBy(x => x.Id)
                : consulta.OrderByDescending(x => x.Status).ThenBy(x => x.Id),
            CampoOrdenacaoIncidente.Atualizado => asc
                ? consulta.OrderBy(x => x.AtualizadoEmUtc).ThenBy(x => x.Id)
                : consulta.OrderByDescending(x => x.AtualizadoEmUtc).ThenBy(x => x.Id),
            _ => asc
                ? consulta.OrderBy(x => x.DetectadoEmUtc).ThenBy(x => x.Id)
                : consulta.OrderByDescending(x => x.DetectadoEmUtc).ThenBy(x => x.Id),
        };

        var itens = await consulta
            .Skip(Paginacao.Saltar(f.Pagina, f.TamanhoPagina))
            .Take(f.TamanhoPagina)
            .Select(x => new ItemIncidente(
                x.Id, x.Codigo, x.Tipo, x.Severidade, x.Status, x.Titulo, x.TenantAfetadoId,
                x.ClassificacaoRisco, x.ResponsavelId, x.DetectadoEmUtc, x.AtualizadoEmUtc, x.EncerradoEmUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ResultadoPaginado<ItemIncidente>(itens, f.Pagina, f.TamanhoPagina, total);
    }
}
