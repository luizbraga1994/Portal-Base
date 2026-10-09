namespace PortalBase.Application.Multitenancy;

/// <summary>
/// Autorização de tenant validada NO SERVIDOR (regra 3, seção 5). Retorna <c>true</c>
/// SOMENTE se o principal autenticado possui vínculo ativo com o tenant E o tenant está
/// operacional. Deny-by-default: qualquer condição ausente ⇒ <c>false</c>. Nunca confia
/// em identificador vindo do cliente.
/// </summary>
public interface IAutorizacaoTenant
{
    ValueTask<bool> PodeAcessarAsync(string principalId, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Autorização ADMINISTRATIVA de tenant (Prompt 09, seção 20): o principal tem vínculo ativo
    /// com o tenant (usuário ativo), SEM exigir que o tenant esteja operacional — pois operações
    /// de ciclo de vida (ativar/reativar) agem justamente sobre tenants NÃO operacionais. A
    /// permissão específica é conferida à parte pelo serviço de autorização. Deny-by-default.
    /// </summary>
    ValueTask<bool> PodeAdministrarAsync(string principalId, Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadados confiáveis do tenant, lidos do catálogo central. Retorna <c>null</c> se o
/// tenant não existir. NUNCA devolve credenciais nem connection string (regra 7).
/// </summary>
public interface ITenantStore
{
    ValueTask<TenantMetadados?> ObterAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolve e AUTORIZA o tenant, preenchendo o <see cref="ITenantContext"/> da requisição.
/// Fluxo obrigatório: principal autenticado → catálogo → vínculo usuário×tenant → status
/// do tenant → metadados → contexto autorizado. Fail-closed: qualquer falha lança e o
/// contexto fica inválido (nenhuma conexão de tenant é liberada).
/// </summary>
public interface IResolvedorTenant
{
    ValueTask<ITenantContext> ResolverAsync(string? principalId, Guid tenantId, CancellationToken cancellationToken = default);
}
