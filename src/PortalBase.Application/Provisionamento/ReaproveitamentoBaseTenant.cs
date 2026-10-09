using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Provisionamento;

/// <summary>
/// Regra (fail-closed) para reaproveitar uma base de tenant que JÁ existe quando o tenant ainda não foi provisionado. Com o
/// nome escolhido (STAKE → <c>portalbase_tenant_stake</c>) a base pode existir antes do tenant — sobra de um catálogo
/// perdido, criação manual, tenant antigo — e os dados nela pertencem a quem a marca de proprietário disser. Um tenant
/// NUNCA herda dados de outro contribuinte: só reaproveita a base vazia, a sua própria (nova tentativa) ou a do MESMO
/// CNPJ (o mesmo contribuinte recadastrado). Base com tabelas e sem marca é de origem desconhecida ⇒ recusada, exceto o
/// nome legado <c>tenant_&lt;id&gt;</c>, derivado do próprio Id (colisão impossível).
/// </summary>
public static class ReaproveitamentoBaseTenant
{
    public static bool Permitido(ProprietarioBaseTenant dono, Guid? tenantId, Cnpj cnpj, string nomeBase)
    {
        ArgumentNullException.ThrowIfNull(dono);
        ArgumentNullException.ThrowIfNull(cnpj);
        ArgumentNullException.ThrowIfNull(nomeBase);

        if (dono.TenantId is Guid donoId)
        {
            return donoId == tenantId || string.Equals(dono.Cnpj, cnpj.Valor, StringComparison.Ordinal);
        }

        return dono.Vazia
            || (tenantId is Guid id && string.Equals(nomeBase, "tenant_" + id.ToString("N"), StringComparison.Ordinal));
    }
}
