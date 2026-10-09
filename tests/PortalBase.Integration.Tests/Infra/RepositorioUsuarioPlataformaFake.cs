using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Integration.Tests.Infra;

/// <summary>
/// Repositório de usuários que só responde quem tem o perfil "Administrador da plataforma" (para testar a
/// autorização de plataforma sem banco). Qualquer outra operação é inesperada nesses testes.
/// </summary>
internal sealed class RepositorioUsuarioPlataformaFake : IRepositorioUsuario
{
    public HashSet<Guid> AdministradoresPlataforma { get; } = [];

    public Task<bool> EhAdministradorPlataformaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => Task.FromResult(AdministradoresPlataforma.Contains(usuarioId));

    public Task<bool> ExisteAdministradorPlataformaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(AdministradoresPlataforma.Count > 0);

    public Task<Usuario?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Usuario?> ObterPorLoginAsync(string login, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Usuario?> ObterPorEmailAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task AdicionarAsync(Usuario usuario, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PermissoesTenant> ObterPermissoesAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task AdicionarCodigosRecuperacaoMfaAsync(IEnumerable<CodigoRecuperacaoMfa> codigos, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<int> RemoverCodigosRecuperacaoMfaAsync(Guid usuarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CodigoRecuperacaoMfa?> ObterCodigoRecuperacaoMfaAsync(Guid usuarioId, string hashCodigo, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task AdicionarTokenRecuperacaoAsync(TokenRecuperacaoConta token, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<TokenRecuperacaoConta?> ObterTokenRecuperacaoAsync(string hashToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<int> ContarTokensRecuperacaoDesdeAsync(Guid usuarioId, DateTimeOffset desde, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> ConsumirTokenRecuperacaoAsync(Guid tokenId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task InvalidarTokensRecuperacaoAsync(Guid usuarioId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
