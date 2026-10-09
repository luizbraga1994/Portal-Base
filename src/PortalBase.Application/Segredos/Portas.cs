namespace PortalBase.Application.Segredos;

/// <summary>Tipos de segredo POR TENANT (o nome é derivado do tenant no servidor — seção 24).</summary>
public enum TipoSegredoTenant
{
    /// <summary>
    /// Segredo de uma integração externa DO TENANT (ex.: senha de um ERP, client_secret de uma API). Modelo do padrão:
    /// o nome no cofre é derivado do tenant no servidor; acrescente um tipo por segredo novo.
    /// </summary>
    SegredoIntegracao = 1,
}

/// <summary>
/// Acesso a um segredo de um TENANT, com garantia de isolamento (seção 24). O chamador informa
/// apenas o <b>tipo</b> e o tenant; o nome do segredo é CONSTRUÍDO NO SERVIDOR a partir do tenant
/// autorizado (nunca uma referência arbitrária do cliente). Autoriza server-side (permissão +
/// tenant + MFA quando aplicável), audita o acesso e NUNCA registra o valor. Fail-closed.
/// </summary>
public interface IAcessoSegredoTenant
{
    Task<string> ObterAsync(Guid tenantId, TipoSegredoTenant tipo, CancellationToken cancellationToken = default);

    /// <summary>
    /// DEFINE/ROTACIONA um segredo do tenant no cofre (o valor NUNCA vai para banco/log). O nome é derivado do
    /// tenant no servidor; autoriza server-side (mesma permissão do tipo) e audita a atualização
    /// (<c>SecretUpdated</c>) SEM registrar o valor. Fail-closed quando o cofre não suporta escrita.
    /// </summary>
    Task DefinirAsync(Guid tenantId, TipoSegredoTenant tipo, string valor, CancellationToken cancellationToken = default);

    /// <summary>
    /// O NOME lógico (referência no cofre) do segredo deste tipo para o tenant — derivado no servidor, para guardar a
    /// referência nos metadados. Nunca o valor.
    /// </summary>
    string NomeDoSegredo(Guid tenantId, TipoSegredoTenant tipo);
}
