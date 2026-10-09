using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;

namespace PortalBase.Application.Consultas;

/// <summary>
/// Guarda comum das consultas de LEITURA no banco do tenant (Prompt 36, §7-8). Centraliza a regra
/// fail-closed: sem <see cref="ITenantContext"/> autorizado, falha; com ele, EXIGE a permissão de
/// leitura NO TENANT autorizado (server-side, nunca a partir de <c>?tenantId=</c> do cliente). Devolve o
/// tenant resolvido para uso interno. Reutilizado por todos os read-models tenant-escopados.
/// </summary>
public static class GuardaConsultaTenant
{
    /// <summary>Resolve o tenant do contexto autorizado e exige a permissão; lança em falha (fail-closed).</summary>
    public static async Task<Guid> ExigirLeituraAsync(
        ITenantContext tenant,
        IServicoAutorizacao autorizacao,
        string permissao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(autorizacao);

        if (!tenant.EstaAutorizado || tenant.TenantId is not Guid tenantId)
        {
            throw new TenantContextAusenteException();
        }

        await autorizacao.ExigirAsync(tenantId, permissao, cancellationToken).ConfigureAwait(false);
        return tenantId;
    }
}
