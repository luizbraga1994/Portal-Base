using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Repositorios;

/// <summary>
/// Repositório de usuários e das permissões efetivas por tenant, sobre o catálogo central.
/// As permissões vêm SEMPRE de uma consulta ao servidor (vínculo ativo + papéis escopados),
/// nunca do cliente (seções 11, 12).
/// </summary>
public sealed class RepositorioUsuario : IRepositorioUsuario
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public RepositorioUsuario(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<Usuario?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await _contexto.Usuarios.FirstOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);

    public async Task<Usuario?> ObterPorLoginAsync(string login, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        return await _contexto.Usuarios.FirstOrDefaultAsync(u => u.Login == login, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Usuario?> ObterPorEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var canonico = Usuario.NormalizarEmail(email);
        return await _contexto.Usuarios.FirstOrDefaultAsync(u => u.Email == canonico, cancellationToken).ConfigureAwait(false);
    }

    public Task AdicionarAsync(Usuario usuario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        _contexto.Usuarios.Add(usuario);
        return Task.CompletedTask;
    }

    public async Task<PermissoesTenant> ObterPermissoesAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Administrador da plataforma (usuário ATIVO): todas as permissões de contribuinte, em qualquer contribuinte.
        // O acesso ao contribuinte em si continua decidido por IAutorizacaoTenant (que exige o contribuinte operacional).
        if (await EhAdministradorPlataformaAsync(usuarioId, cancellationToken).ConfigureAwait(false))
        {
            var todas = await _contexto.Permissoes.AsNoTracking().Select(p => p.Chave).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new PermissoesTenant(todas);
        }

        // Somente vínculo ATIVO; papéis escopados ao vínculo; permissões dos papéis.
        var chaves = await (
            from ut in _contexto.UsuarioTenants.AsNoTracking()
            where ut.UsuarioId == usuarioId && ut.TenantId == tenantId && ut.Status == StatusAcesso.Ativo
            join utp in _contexto.UsuarioTenantPapeis.AsNoTracking() on ut.Id equals utp.UsuarioTenantId
            join pp in _contexto.PapelPermissoes.AsNoTracking() on utp.PapelId equals pp.PapelId
            join p in _contexto.Permissoes.AsNoTracking() on pp.PermissaoId equals p.Id
            select p.Chave)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return chaves.Count == 0 ? PermissoesTenant.Nenhuma : new PermissoesTenant(chaves);
    }

    public async Task<bool> EhAdministradorPlataformaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => usuarioId != Guid.Empty
            && await _contexto.Usuarios
                .AsNoTracking()
                .AnyAsync(u => u.Id == usuarioId && u.AdministradorPlataforma && u.Status == StatusUsuario.Ativo, cancellationToken)
                .ConfigureAwait(false);

    public Task<bool> ExisteAdministradorPlataformaAsync(CancellationToken cancellationToken = default)
        => _contexto.Usuarios.AsNoTracking().AnyAsync(u => u.AdministradorPlataforma, cancellationToken);

    public Task AdicionarCodigosRecuperacaoMfaAsync(IEnumerable<CodigoRecuperacaoMfa> codigos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codigos);
        _contexto.CodigosRecuperacaoMfa.AddRange(codigos);
        return Task.CompletedTask;
    }

    public async Task<int> RemoverCodigosRecuperacaoMfaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => await _contexto.CodigosRecuperacaoMfa
            .Where(c => c.UsuarioId == usuarioId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<CodigoRecuperacaoMfa?> ObterCodigoRecuperacaoMfaAsync(Guid usuarioId, string hashCodigo, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashCodigo);
        return await _contexto.CodigosRecuperacaoMfa
            .FirstOrDefaultAsync(c => c.UsuarioId == usuarioId && c.HashCodigo == hashCodigo, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task AdicionarTokenRecuperacaoAsync(TokenRecuperacaoConta token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        _contexto.TokensRecuperacao.Add(token);
        return Task.CompletedTask;
    }

    public async Task<TokenRecuperacaoConta?> ObterTokenRecuperacaoAsync(string hashToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashToken);
        return await _contexto.TokensRecuperacao
            .FirstOrDefaultAsync(t => t.HashToken == hashToken, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> ContarTokensRecuperacaoDesdeAsync(Guid usuarioId, DateTimeOffset desde, CancellationToken cancellationToken = default)
        => await _contexto.TokensRecuperacao.AsNoTracking()
            .CountAsync(t => t.UsuarioId == usuarioId && t.CriadoEmUtc >= desde, cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> ConsumirTokenRecuperacaoAsync(Guid tokenId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
    {
        // UPDATE ... WHERE UsadoEmUtc IS NULL AND ExpiraEmUtc > agora: o banco decide quem consome (sem corrida).
        var afetados = await _contexto.TokensRecuperacao
            .Where(t => t.Id == tokenId && t.UsadoEmUtc == null && t.ExpiraEmUtc > agoraUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsadoEmUtc, agoraUtc), cancellationToken)
            .ConfigureAwait(false);
        return afetados == 1;
    }

    public Task InvalidarTokensRecuperacaoAsync(Guid usuarioId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
        => _contexto.TokensRecuperacao
            .Where(t => t.UsuarioId == usuarioId && t.UsadoEmUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsadoEmUtc, agoraUtc), cancellationToken);
}
