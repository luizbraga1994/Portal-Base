using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Identidade;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Repositorios;

/// <summary>
/// Persistência do cadastro de usuários e perfis no CATÁLOGO (LINQ parametrizado — nunca SQL concatenado). As
/// consultas de perfis são sempre escopadas: papéis do sistema (TenantId nulo) + os do contribuinte informado.
/// Entidades devolvidas por "Obter…" são rastreadas (a unidade de trabalho grava as alterações).
/// </summary>
public sealed class RepositorioGestaoUsuarios : IRepositorioGestaoUsuarios
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public RepositorioGestaoUsuarios(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<Usuario?> ObterUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => await _contexto.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, cancellationToken).ConfigureAwait(false);

    public async Task<bool> EmailEmUsoAsync(string emailNormalizado, Guid? exceto, CancellationToken cancellationToken = default)
        => await _contexto.Usuarios.AsNoTracking()
            .AnyAsync(u => u.Email == emailNormalizado && (exceto == null || u.Id != exceto), cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> LoginEmUsoAsync(string login, Guid? exceto, CancellationToken cancellationToken = default)
        => await _contexto.Usuarios.AsNoTracking()
            .AnyAsync(u => u.Login == login && (exceto == null || u.Id != exceto), cancellationToken)
            .ConfigureAwait(false);

    public Task AdicionarUsuarioAsync(Usuario usuario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        _contexto.Usuarios.Add(usuario);
        return Task.CompletedTask;
    }

    public async Task<UsuarioTenant?> ObterVinculoAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default)
        => await _contexto.UsuarioTenants
            .FirstOrDefaultAsync(v => v.UsuarioId == usuarioId && v.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);

    public Task AdicionarVinculoAsync(UsuarioTenant vinculo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vinculo);
        _contexto.UsuarioTenants.Add(vinculo);
        return Task.CompletedTask;
    }

    public Task RemoverVinculoAsync(UsuarioTenant vinculo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vinculo);
        _contexto.UsuarioTenants.Remove(vinculo); // os perfis do vínculo saem em cascata
        return Task.CompletedTask;
    }

    public async Task<bool> TemVinculoComOutroContribuinteAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default)
        => await _contexto.UsuarioTenants.AsNoTracking()
            .AnyAsync(v => v.UsuarioId == usuarioId && v.TenantId != tenantId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<VinculoDoUsuario>> ListarVinculosDoUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var vinculos = await _contexto.UsuarioTenants.AsNoTracking()
            .Where(v => v.UsuarioId == usuarioId)
            .Select(v => new { v.Id, v.TenantId, v.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (vinculos.Count == 0)
        {
            return [];
        }

        var ids = vinculos.Select(v => v.Id).ToList();
        var perfis = await (
                from utp in _contexto.UsuarioTenantPapeis.AsNoTracking()
                join papel in _contexto.Papeis.AsNoTracking() on utp.PapelId equals papel.Id
                where ids.Contains(utp.UsuarioTenantId)
                select new { utp.UsuarioTenantId, papel.Id, papel.Nome, DoSistema = papel.TenantId == null })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var porVinculo = perfis.ToLookup(p => p.UsuarioTenantId);

        return vinculos
            .Select(v => new VinculoDoUsuario(
                v.TenantId,
                v.Status == StatusAcesso.Ativo,
                [.. porVinculo[v.Id]
                    .OrderBy(p => p.DoSistema ? 0 : 1)
                    .ThenBy(p => p.DoSistema ? OrdemDoSistema(p.Nome) : 0)
                    .ThenBy(p => p.Nome, StringComparer.CurrentCultureIgnoreCase)
                    .Select(p => new PerfilDoVinculo(p.Id, p.Nome, p.DoSistema))]))
            .ToList();
    }

    public async Task<IReadOnlyList<Guid>> PerfisDoVinculoAsync(Guid usuarioTenantId, CancellationToken cancellationToken = default)
        => await _contexto.UsuarioTenantPapeis.AsNoTracking()
            .Where(p => p.UsuarioTenantId == usuarioTenantId)
            .Select(p => p.PapelId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task DefinirPerfisDoVinculoAsync(Guid usuarioTenantId, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(perfis);
        var atuais = await _contexto.UsuarioTenantPapeis
            .Where(p => p.UsuarioTenantId == usuarioTenantId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _contexto.UsuarioTenantPapeis.RemoveRange(atuais.Where(a => !perfis.Contains(a.PapelId)));
        foreach (var novo in perfis.Where(p => atuais.TrueForAll(a => a.PapelId != p)))
        {
            _contexto.UsuarioTenantPapeis.Add(new UsuarioTenantPapel(usuarioTenantId, novo));
        }
    }

    public async Task<IReadOnlyList<PerfilGestao>> ListarPerfisAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var perfis = await _contexto.Papeis.AsNoTracking()
            .Where(p => p.TenantId == null || p.TenantId == tenantId)
            .Select(p => new { p.Id, p.Nome, p.Descricao, p.TenantId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = perfis.Select(p => p.Id).ToList();

        var permissoes = await (
                from pp in _contexto.PapelPermissoes.AsNoTracking()
                join permissao in _contexto.Permissoes.AsNoTracking() on pp.PermissaoId equals permissao.Id
                where ids.Contains(pp.PapelId)
                select new { pp.PapelId, permissao.Chave })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var porPerfil = permissoes.ToLookup(p => p.PapelId, p => p.Chave);

        // Usuários com ACESSO ATIVO a este contribuinte que têm cada perfil.
        var usuarios = await (
                from utp in _contexto.UsuarioTenantPapeis.AsNoTracking()
                join ut in _contexto.UsuarioTenants.AsNoTracking() on utp.UsuarioTenantId equals ut.Id
                where ut.TenantId == tenantId && ut.Status == StatusAcesso.Ativo && ids.Contains(utp.PapelId)
                group utp by utp.PapelId into g
                select new { PapelId = g.Key, Total = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var totais = usuarios.ToDictionary(u => u.PapelId, u => u.Total);

        return perfis
            .OrderBy(p => p.TenantId == null ? 0 : 1)
            .ThenBy(p => p.TenantId == null ? OrdemDoSistema(p.Nome) : 0)
            .ThenBy(p => p.Nome, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PerfilGestao(
                p.Id,
                p.Nome,
                p.Descricao,
                p.TenantId == null,
                [.. porPerfil[p.Id].OrderBy(c => c, StringComparer.Ordinal)],
                totais.GetValueOrDefault(p.Id)))
            .ToList();
    }

    public async Task<Papel?> ObterPerfilAsync(Guid perfilId, CancellationToken cancellationToken = default)
        => await _contexto.Papeis.FirstOrDefaultAsync(p => p.Id == perfilId, cancellationToken).ConfigureAwait(false);

    public async Task<bool> NomePerfilEmUsoAsync(Guid tenantId, string nome, Guid? exceto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nome);

        // A collation do catálogo diferencia maiúsculas; os perfis visíveis a um contribuinte são poucos, então a
        // comparação sem diferenciar maiúsculas é feita aqui (sistema + deste contribuinte).
        var nomes = await _contexto.Papeis.AsNoTracking()
            .Where(p => (p.TenantId == null || p.TenantId == tenantId) && (exceto == null || p.Id != exceto))
            .Select(p => p.Nome)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var alvo = nome.Trim();
        return nomes.Exists(n => string.Equals(n, alvo, StringComparison.OrdinalIgnoreCase));
    }

    public Task AdicionarPerfilAsync(Papel perfil, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(perfil);
        _contexto.Papeis.Add(perfil);
        return Task.CompletedTask;
    }

    public Task RemoverPerfilAsync(Papel perfil, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(perfil);
        _contexto.Papeis.Remove(perfil); // as permissões do perfil saem em cascata
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<string, Guid>> PermissoesPorChaveAsync(CancellationToken cancellationToken = default)
    {
        var todas = await _contexto.Permissoes.AsNoTracking()
            .Select(p => new { p.Chave, p.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return todas
            .Where(p => !Permissoes.EhPlataforma(p.Chave))
            .ToDictionary(p => p.Chave, p => p.Id, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ChavesDosPerfisAsync(IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(perfis);
        if (perfis.Count == 0)
        {
            return [];
        }

        var ids = perfis.ToList();
        return await (
                from pp in _contexto.PapelPermissoes.AsNoTracking()
                join permissao in _contexto.Permissoes.AsNoTracking() on pp.PermissaoId equals permissao.Id
                where ids.Contains(pp.PapelId)
                select permissao.Chave)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DefinirPermissoesDoPerfilAsync(Guid perfilId, IReadOnlyCollection<Guid> permissoes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissoes);
        var atuais = await _contexto.PapelPermissoes
            .Where(pp => pp.PapelId == perfilId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _contexto.PapelPermissoes.RemoveRange(atuais.Where(a => !permissoes.Contains(a.PermissaoId)));
        foreach (var nova in permissoes.Where(p => atuais.TrueForAll(a => a.PermissaoId != p)))
        {
            _contexto.PapelPermissoes.Add(new PapelPermissao(perfilId, nova));
        }
    }

    public async Task<int> ContarVinculosComPerfilAsync(Guid perfilId, CancellationToken cancellationToken = default)
        => await _contexto.UsuarioTenantPapeis.AsNoTracking()
            .CountAsync(p => p.PapelId == perfilId, cancellationToken)
            .ConfigureAwait(false);

    // Leitor, Analista, Administrador — a ordem de "quanto acesso dá".
    private static int OrdemDoSistema(string nome)
        => Enum.TryParse<PapelConhecido>(nome, out var papel) ? (int)papel : int.MaxValue;
}
