using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Administracao;
using PortalBase.Application.Consultas;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Administracao;

/// <summary>
/// Consulta administrativa de USUÁRIOS por tenant (Prompt 72) sobre o catálogo central. É SOMENTE
/// LEITURA (<c>AsNoTracking</c>), escopada por <c>UsuarioTenant.TenantId</c> (o catálogo é COMPARTILHADO,
/// então o isolamento é aplicado na consulta), com ordenação estável (desempate por Id), paginação NO
/// BANCO (nunca em memória) e projeção SQL apenas de campos seguros. Os papéis do vínculo são carregados
/// em UMA consulta adicional para a página (sem N+1). NUNCA projeta hash/segredo/stamp.
/// </summary>
public sealed class ConsultaUsuariosAdmin : IConsultaUsuariosAdmin
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public ConsultaUsuariosAdmin(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<ResultadoPaginado<UsuarioAdministracaoDto>> ListarPorTenantAsync(
        Guid tenantId,
        FiltroUsuariosAdmin filtro,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);

        var consulta = FiltrarBase(tenantId, filtro);

        var total = await consulta.LongCountAsync(cancellationToken).ConfigureAwait(false);
        if (total == 0)
        {
            return ResultadoPaginado.Vazio<UsuarioAdministracaoDto>(filtro.Pagina, filtro.TamanhoPagina);
        }

        var ordenada = Ordenar(consulta, filtro);
        var linhas = await ordenada
            .Skip((filtro.Pagina - 1) * filtro.TamanhoPagina)
            .Take(filtro.TamanhoPagina)
            .Select(x => new LinhaUsuario(
                x.u.Id, x.u.Login, x.u.Nome, x.u.Email, x.u.Status, x.ut.Status,
                x.u.MfaHabilitado, x.u.BloqueadoAteUtc, x.u.CriadoEmUtc, x.u.UltimoAcessoUtc,
                x.ut.Id, x.ut.CriadoEmUtc, x.u.AdministradorPlataforma, x.u.HashSenha != null, x.u.MfaObrigatorio, x.u.TrocarSenhaNoProximoAcesso))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var papeis = await CarregarPapeisAsync(linhas.Select(l => l.VinculoId), cancellationToken).ConfigureAwait(false);
        var itens = linhas.Select(l => Projetar(l, papeis)).ToList();

        return new ResultadoPaginado<UsuarioAdministracaoDto>(itens, filtro.Pagina, filtro.TamanhoPagina, total);
    }

    public async Task<UsuarioAdministracaoDto?> ObterDoTenantAsync(
        Guid tenantId,
        Guid usuarioId,
        CancellationToken cancellationToken = default)
    {
        var linha = await (
                from ut in _contexto.UsuarioTenants.AsNoTracking()
                join u in _contexto.Usuarios.AsNoTracking() on ut.UsuarioId equals u.Id
                where ut.TenantId == tenantId && u.Id == usuarioId
                select new LinhaUsuario(
                    u.Id, u.Login, u.Nome, u.Email, u.Status, ut.Status,
                    u.MfaHabilitado, u.BloqueadoAteUtc, u.CriadoEmUtc, u.UltimoAcessoUtc,
                    ut.Id, ut.CriadoEmUtc, u.AdministradorPlataforma, u.HashSenha != null, u.MfaObrigatorio, u.TrocarSenhaNoProximoAcesso))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (linha is null)
        {
            return null; // sem vínculo com o tenant corrente → não é visível (isolamento/IDOR)
        }

        var papeis = await CarregarPapeisAsync([linha.VinculoId], cancellationToken).ConfigureAwait(false);
        var outros = await _contexto.UsuarioTenants.AsNoTracking()
            .AnyAsync(v => v.UsuarioId == usuarioId && v.TenantId != tenantId, cancellationToken)
            .ConfigureAwait(false);
        return Projetar(linha, papeis) with { TemOutrosContribuintes = outros };
    }

    private IQueryable<LinhaBase> FiltrarBase(Guid tenantId, FiltroUsuariosAdmin filtro)
    {
        var consulta =
            from ut in _contexto.UsuarioTenants.AsNoTracking()
            join u in _contexto.Usuarios.AsNoTracking() on ut.UsuarioId equals u.Id
            where ut.TenantId == tenantId
            select new LinhaBase { ut = ut, u = u };

        if (filtro.Status is StatusUsuario status)
        {
            consulta = consulta.Where(x => x.u.Status == status);
        }

        if (filtro.StatusAcesso is StatusAcesso statusAcesso)
        {
            consulta = consulta.Where(x => x.ut.Status == statusAcesso);
        }

        if (filtro.Busca is { Length: > 0 } busca)
        {
            // Parametrizado (nunca SQL concatenado — regra 13); termo limitado no Normalizado (§6).
            consulta = consulta.Where(x => x.u.Login.Contains(busca) || x.u.Nome.Contains(busca) || x.u.Email.Contains(busca));
        }

        return consulta;
    }

    // Ordenação por WHITELIST (enum) + desempate ESTÁVEL por Id — nunca campo arbitrário do cliente (§5).
    private static IOrderedQueryable<LinhaBase> Ordenar(IQueryable<LinhaBase> consulta, FiltroUsuariosAdmin filtro)
    {
        var desc = filtro.Descendente;
        IOrderedQueryable<LinhaBase> ordenada = filtro.Ordenacao switch
        {
            CampoOrdenacaoUsuario.Nome => desc ? consulta.OrderByDescending(x => x.u.Nome) : consulta.OrderBy(x => x.u.Nome),
            CampoOrdenacaoUsuario.Status => desc ? consulta.OrderByDescending(x => x.u.Status) : consulta.OrderBy(x => x.u.Status),
            CampoOrdenacaoUsuario.CriadoEm => desc ? consulta.OrderByDescending(x => x.u.CriadoEmUtc) : consulta.OrderBy(x => x.u.CriadoEmUtc),
            CampoOrdenacaoUsuario.UltimoAcesso => desc ? consulta.OrderByDescending(x => x.u.UltimoAcessoUtc) : consulta.OrderBy(x => x.u.UltimoAcessoUtc),
            _ => desc ? consulta.OrderByDescending(x => x.u.Login) : consulta.OrderBy(x => x.u.Login),
        };
        return ordenada.ThenBy(x => x.u.Id);
    }

    private async Task<ILookup<Guid, (Guid Id, string Nome)>> CarregarPapeisAsync(IEnumerable<Guid> vinculoIds, CancellationToken cancellationToken)
    {
        var ids = vinculoIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return Enumerable.Empty<(Guid Vinculo, Guid Id, string Nome)>().ToLookup(x => x.Vinculo, x => (x.Id, x.Nome));
        }

        var pares = await (
                from utp in _contexto.UsuarioTenantPapeis.AsNoTracking()
                join p in _contexto.Papeis.AsNoTracking() on utp.PapelId equals p.Id
                where ids.Contains(utp.UsuarioTenantId)
                select new { utp.UsuarioTenantId, p.Id, p.Nome })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return pares.ToLookup(x => x.UsuarioTenantId, x => (x.Id, x.Nome));
    }

    private static UsuarioAdministracaoDto Projetar(LinhaUsuario l, ILookup<Guid, (Guid Id, string Nome)> papeis)
    {
        var doVinculo = papeis[l.VinculoId].OrderBy(p => p.Nome, StringComparer.Ordinal).ToList();
        return new(
            l.UsuarioId,
            l.Login,
            l.Nome,
            l.Email,
            l.Status,
            l.StatusAcesso,
            l.MfaHabilitado,
            l.BloqueadoAteUtc,
            [.. doVinculo.Select(p => p.Nome)],
            l.CriadoEmUtc,
            l.UltimoAcessoUtc,
            l.VinculoCriadoEmUtc,
            l.AdministradorPlataforma,
            TemOutrosContribuintes: false,
            [.. doVinculo.Select(p => p.Id)],
            l.PossuiSenha,
            l.MfaObrigatorio,
            l.SenhaTemporaria);
    }

    // Projeção intermediária (SQL) — só campos seguros; nunca hash/segredo/stamp (só SE há senha, não ela).
    private sealed record LinhaUsuario(
        Guid UsuarioId, string Login, string Nome, string Email, StatusUsuario Status, StatusAcesso StatusAcesso,
        bool MfaHabilitado, DateTimeOffset? BloqueadoAteUtc, DateTimeOffset CriadoEmUtc, DateTimeOffset? UltimoAcessoUtc,
        Guid VinculoId, DateTimeOffset VinculoCriadoEmUtc, bool AdministradorPlataforma, bool PossuiSenha, bool MfaObrigatorio, bool SenhaTemporaria);

    // Forma de junção para filtro/ordenação antes da projeção final.
    private sealed class LinhaBase
    {
#pragma warning disable IDE1006, SA1300 // nomes minúsculos deliberados (aliases da junção)
        public UsuarioTenant ut { get; init; } = null!;
        public Usuario u { get; init; } = null!;
#pragma warning restore IDE1006, SA1300
    }
}
