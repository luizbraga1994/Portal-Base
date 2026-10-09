namespace PortalBase.Application.Multitenancy;

/// <summary>
/// Implementação com escopo de requisição do <see cref="ITenantContext"/>. Deny-by-default:
/// nasce <see cref="EstadoTenant.NaoResolvido"/> e só expõe metadados de conexão quando
/// <see cref="EstadoTenant.Autorizado"/>. NÃO pode ser singleton nem compartilhado entre
/// usuários/tenants (registrar como Scoped).
/// </summary>
public sealed class ContextoTenant : IContextoTenantMutavel
{
    public EstadoTenant Estado { get; private set; } = EstadoTenant.NaoResolvido;

    public string? PrincipalId { get; private set; }

    public Guid? TenantId { get; private set; }

    public string? NomeBaseFisica { get; private set; }

    public string? ReferenciaSegredoConexao { get; private set; }

    public bool EstaAutorizado => Estado == EstadoTenant.Autorizado;

    public void Reiniciar()
    {
        Estado = EstadoTenant.NaoResolvido;
        PrincipalId = null;
        TenantId = null;
        NomeBaseFisica = null;
        ReferenciaSegredoConexao = null;
    }

    public void MarcarResolvido(string principalId, Guid tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        // Resolvido: sabe-se que o tenant existe, mas AINDA não autorizado —
        // por isso nenhum metadado de conexão é exposto.
        Estado = EstadoTenant.Resolvido;
        PrincipalId = principalId;
        TenantId = tenantId;
        NomeBaseFisica = null;
        ReferenciaSegredoConexao = null;
    }

    public void MarcarAutorizado(string principalId, TenantMetadados metadados)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentNullException.ThrowIfNull(metadados);

        Estado = EstadoTenant.Autorizado;
        PrincipalId = principalId;
        TenantId = metadados.Id;
        NomeBaseFisica = metadados.NomeBaseFisica;
        ReferenciaSegredoConexao = metadados.ReferenciaSegredoConexao;
    }

    public void MarcarInvalido(string? principalId)
    {
        Estado = EstadoTenant.Invalido;
        PrincipalId = principalId;
        TenantId = null;
        NomeBaseFisica = null;
        ReferenciaSegredoConexao = null;
    }
}
