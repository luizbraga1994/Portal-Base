using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Auditoria;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Auditoria;

/// <summary>
/// Consulta da trilha sobre o catálogo (seção 16). SEMPRE filtra por tenant, pagina e usa LINQ
/// parametrizado (regra 13). Nunca carrega a tabela inteira; nunca aceita paginação ilimitada
/// (o filtro é normalizado pela camada de aplicação antes de chegar aqui).
/// </summary>
public sealed class ConsultaAuditoriaEfCore : IConsultaAuditoria
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public ConsultaAuditoriaEfCore(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<PaginaAuditoria> ConsultarAsync(FiltroAuditoria filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var f = filtro.Normalizado();

        // Isolamento: o filtro de tenant é obrigatório e sempre aplicado (nunca cross-tenant).
        var consulta = _contexto.RegistrosAuditoria.AsNoTracking().Where(r => r.Tenant == f.Tenant);

        if (f.Desde is { } desde)
        {
            var d = desde.UtcDateTime;
            consulta = consulta.Where(r => r.OcorridoEmUtc >= d);
        }

        if (f.Ate is { } ate)
        {
            var a = ate.UtcDateTime;
            consulta = consulta.Where(r => r.OcorridoEmUtc <= a);
        }

        if (f.Atores is { Count: > 0 } atores)
        {
            // Resolvidos pelo servidor (identificador digitado + usuários cujo nome/login/e-mail casou) — IN parametrizado.
            var lista = atores.ToList();
            consulta = consulta.Where(r => lista.Contains(r.PrincipalId));
        }
        else if (!string.IsNullOrWhiteSpace(f.Ator))
        {
            consulta = consulta.Where(r => r.PrincipalId == f.Ator);
        }

        if (!string.IsNullOrWhiteSpace(f.Acao))
        {
            consulta = consulta.Where(r => r.Operacao == f.Acao);
        }

        if (!string.IsNullOrWhiteSpace(f.Resultado))
        {
            consulta = consulta.Where(r => r.Resultado == f.Resultado);
        }

        if (!string.IsNullOrWhiteSpace(f.TipoRecurso))
        {
            consulta = consulta.Where(r => r.TipoRecurso == f.TipoRecurso);
        }

        if (!string.IsNullOrWhiteSpace(f.RecursoId))
        {
            consulta = consulta.Where(r => r.RecursoId == f.RecursoId);
        }

        if (!string.IsNullOrWhiteSpace(f.CorrelationId))
        {
            consulta = consulta.Where(r => r.CorrelationId == f.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(f.Protocolo))
        {
            consulta = consulta.Where(r => r.Protocolo == f.Protocolo);
        }

        if (!string.IsNullOrWhiteSpace(f.Competencia))
        {
            consulta = consulta.Where(r => r.Competencia == f.Competencia);
        }

        var total = await consulta.LongCountAsync(cancellationToken).ConfigureAwait(false);

        consulta = f.DescendentePorData
            ? consulta.OrderByDescending(r => r.OcorridoEmUtc).ThenByDescending(r => r.Id)
            : consulta.OrderBy(r => r.OcorridoEmUtc).ThenBy(r => r.Id);

        var itens = await consulta
            .Skip((f.Pagina - 1) * f.TamanhoPagina)
            .Take(f.TamanhoPagina)
            .Select(r => new RegistroAuditoriaDto(
                r.Id,
                r.Tenant,
                r.PrincipalId,
                (TipoAtor)r.TipoAtor,
                r.Operacao,
                r.TipoRecurso,
                r.RecursoId,
                r.Resultado,
                r.Motivo,
                r.CorrelationId,
                r.TraceId,
                r.Protocolo,
                r.Competencia,
                new DateTimeOffset(r.OcorridoEmUtc, TimeSpan.Zero)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PaginaAuditoria(itens, f.Pagina, f.TamanhoPagina, total);
    }
}
