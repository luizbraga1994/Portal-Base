using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;

namespace PortalBase.Application.Multitenancy;

/// <summary>
/// Pipeline de resolução/autorização de tenant (seções 5, 6, 7, 33). Sempre reinicia o
/// contexto antes (invalidando o anterior — troca explícita) e aplica deny-by-default.
/// </summary>
public sealed class ResolvedorTenant : IResolvedorTenant
{
    private const string Operacao = "ResolverTenant";

    private readonly ITenantStore _store;
    private readonly IAutorizacaoTenant _autorizacao;
    private readonly IContextoTenantMutavel _contexto;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public ResolvedorTenant(
        ITenantStore store,
        IAutorizacaoTenant autorizacao,
        IContextoTenantMutavel contexto,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _store = store;
        _autorizacao = autorizacao;
        _contexto = contexto;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    public async ValueTask<ITenantContext> ResolverAsync(
        string? principalId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        // Troca explícita / deny-by-default: invalida qualquer contexto anterior.
        _contexto.Reiniciar();

        if (string.IsNullOrWhiteSpace(principalId))
        {
            _contexto.MarcarInvalido(null);
            throw new NaoAutenticadoException();
        }

        if (tenantId == Guid.Empty)
        {
            _contexto.MarcarInvalido(principalId);
            throw new TenantNaoEncontradoException();
        }

        var metadados = await _store.ObterAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (metadados is null)
        {
            _contexto.MarcarInvalido(principalId);
            throw new TenantNaoEncontradoException();
        }

        _contexto.MarcarResolvido(principalId, tenantId);

        var autorizado = await _autorizacao
            .PodeAcessarAsync(principalId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var agora = _relogio.AgoraUtc;

        if (!autorizado)
        {
            await _auditoria
                .RegistrarAsync(EventoAuditoria.Negado(principalId, tenantId.ToString(), Operacao, agora), cancellationToken)
                .ConfigureAwait(false);
            _contexto.MarcarInvalido(principalId);
            throw new TenantNaoAutorizadoException();
        }

        _contexto.MarcarAutorizado(principalId, metadados);
        await _auditoria
            .RegistrarAsync(EventoAuditoria.Permitido(principalId, tenantId.ToString(), Operacao, agora), cancellationToken)
            .ConfigureAwait(false);

        return _contexto;
    }
}
