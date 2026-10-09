using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;

namespace PortalBase.Application.Autorizacao;

/// <summary>Motivo de negação de uma autorização (para diagnóstico interno — nunca vaza ao usuário).</summary>
public enum MotivoNegacao
{
    Nenhum = 0,
    NaoAutenticado = 1,
    TenantNaoAutorizado = 2,
    SemPermissao = 3,
    MfaRequerido = 4,
}

/// <summary>Decisão de autorização avaliada no servidor.</summary>
public sealed record ResultadoAutorizacao(bool Permitido, MotivoNegacao Motivo)
{
    public static readonly ResultadoAutorizacao Ok = new(true, MotivoNegacao.Nenhum);

    public static ResultadoAutorizacao Negar(MotivoNegacao motivo) => new(false, motivo);
}

/// <summary>
/// Autorização server-side de uma operação sobre um tenant (seções 12, 16, 22). SEMPRE
/// combina: principal autenticado + vínculo ativo com o tenant + permissão exigida + (quando
/// a permissão for de alto impacto) segundo fator satisfeito. Deny-by-default e auditado.
/// A UI pode usar <see cref="IServicoAutorizacao.AvaliarAsync"/> para esconder botões; a
/// decisão real é <see cref="IServicoAutorizacao.ExigirAsync"/>, chamada pelos casos de uso.
/// </summary>
public interface IServicoAutorizacao
{
    ValueTask<ResultadoAutorizacao> AvaliarAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default);

    /// <summary>Fail-closed: lança a exceção correspondente se a operação não for autorizada.</summary>
    ValueTask ExigirAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default);

    /// <summary>
    /// Avalia uma operação ADMINISTRATIVA de tenant (Prompt 09, seção 20). Igual a
    /// <see cref="AvaliarAsync"/>, mas NÃO exige que o tenant esteja operacional — o ciclo de
    /// vida (ativar/reativar/desabilitar) age sobre tenants não operacionais. Exige principal
    /// autenticado + vínculo ativo + permissão (+ MFA se de alto impacto). Deny-by-default.
    /// </summary>
    ValueTask<ResultadoAutorizacao> AvaliarAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default);

    /// <summary>Fail-closed administrativo: lança se a operação de ciclo de vida não for autorizada.</summary>
    ValueTask ExigirAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IServicoAutorizacao"/>
public sealed class ServicoAutorizacao : IServicoAutorizacao
{
    private readonly IContextoSeguranca _seguranca;
    private readonly IAutorizacaoTenant _autorizacaoTenant;
    private readonly IRepositorioUsuario _usuarios;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public ServicoAutorizacao(
        IContextoSeguranca seguranca,
        IAutorizacaoTenant autorizacaoTenant,
        IRepositorioUsuario usuarios,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(autorizacaoTenant);
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _seguranca = seguranca;
        _autorizacaoTenant = autorizacaoTenant;
        _usuarios = usuarios;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    public ValueTask<ResultadoAutorizacao> AvaliarAsync(
        Guid tenantId,
        string permissao,
        CancellationToken cancellationToken = default)
        => AvaliarInternoAsync(tenantId, permissao, administrativo: false, cancellationToken);

    public ValueTask ExigirAsync(
        Guid tenantId,
        string permissao,
        CancellationToken cancellationToken = default)
        => ExigirInternoAsync(tenantId, permissao, administrativo: false, cancellationToken);

    public ValueTask<ResultadoAutorizacao> AvaliarAdministrativoAsync(
        Guid tenantId,
        string permissao,
        CancellationToken cancellationToken = default)
        => AvaliarInternoAsync(tenantId, permissao, administrativo: true, cancellationToken);

    public ValueTask ExigirAdministrativoAsync(
        Guid tenantId,
        string permissao,
        CancellationToken cancellationToken = default)
        => ExigirInternoAsync(tenantId, permissao, administrativo: true, cancellationToken);

    private async ValueTask<ResultadoAutorizacao> AvaliarInternoAsync(
        Guid tenantId,
        string permissao,
        bool administrativo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissao);

        if (!_seguranca.EstaAutenticado
            || string.IsNullOrWhiteSpace(_seguranca.PrincipalId)
            || !Guid.TryParse(_seguranca.PrincipalId, out var usuarioId))
        {
            return ResultadoAutorizacao.Negar(MotivoNegacao.NaoAutenticado);
        }

        // Acesso funcional exige tenant operacional; administrativo exige apenas vínculo ativo
        // (o ciclo de vida age sobre tenants ainda NÃO operacionais — seção 20).
        var podeTenant = administrativo
            ? await _autorizacaoTenant.PodeAdministrarAsync(_seguranca.PrincipalId, tenantId, cancellationToken).ConfigureAwait(false)
            : await _autorizacaoTenant.PodeAcessarAsync(_seguranca.PrincipalId, tenantId, cancellationToken).ConfigureAwait(false);
        if (!podeTenant)
        {
            return ResultadoAutorizacao.Negar(MotivoNegacao.TenantNaoAutorizado);
        }

        var permissoes = await _usuarios
            .ObterPermissoesAsync(usuarioId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!permissoes.Possui(permissao))
        {
            return ResultadoAutorizacao.Negar(MotivoNegacao.SemPermissao);
        }

        // Step-up: papel/permissão nunca bastam para alto impacto sem MFA satisfeito — exceto para o perfil global
        // "Administrador da plataforma" (lido do catálogo), que tem acesso total sem segundo fator.
        if (Permissoes.RequerMfa(permissao)
            && !_seguranca.MfaSatisfeito
            && !await _usuarios.EhAdministradorPlataformaAsync(usuarioId, cancellationToken).ConfigureAwait(false))
        {
            return ResultadoAutorizacao.Negar(MotivoNegacao.MfaRequerido);
        }

        return ResultadoAutorizacao.Ok;
    }

    private async ValueTask ExigirInternoAsync(
        Guid tenantId,
        string permissao,
        bool administrativo,
        CancellationToken cancellationToken)
    {
        var resultado = await AvaliarInternoAsync(tenantId, permissao, administrativo, cancellationToken).ConfigureAwait(false);
        if (resultado.Permitido)
        {
            return;
        }

        var principal = _seguranca.PrincipalId ?? "anonimo";
        await _auditoria
            .RegistrarAsync(
                EventoAuditoria.Negado(principal, tenantId.ToString(), $"Autz:{permissao}", _relogio.AgoraUtc),
                cancellationToken)
            .ConfigureAwait(false);

        throw resultado.Motivo switch
        {
            MotivoNegacao.NaoAutenticado => new NaoAutenticadoException(),
            MotivoNegacao.TenantNaoAutorizado => new TenantNaoAutorizadoException(),
            MotivoNegacao.MfaRequerido => new MfaRequeridoException(),
            _ => new AutorizacaoNegadaException(),
        };
    }
}
