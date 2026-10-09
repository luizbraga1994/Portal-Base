namespace PortalBase.Application.Multitenancy;

/// <summary>Estados do contexto de tenant. Deny-by-default: começa em <see cref="NaoResolvido"/>.</summary>
public enum EstadoTenant
{
    NaoResolvido,
    Resolvido,
    Autorizado,
    Invalido,
}

/// <summary>Metadados mínimos do tenant para localizar a base — NUNCA credenciais (regra 7).</summary>
public sealed record TenantMetadados(Guid Id, string NomeBaseFisica, string ReferenciaSegredoConexao, bool Operacional);

/// <summary>
/// Contexto de tenant com escopo de requisição/operação. Representa o estado da
/// resolução e, quando <see cref="EstadoTenant.Autorizado"/>, os metadados mínimos da
/// base. A connection string NUNCA é exposta aqui. Um identificador informado não torna
/// o tenant válido — só o estado <see cref="EstadoTenant.Autorizado"/> autoriza operação.
/// </summary>
public interface ITenantContext
{
    EstadoTenant Estado { get; }

    string? PrincipalId { get; }

    Guid? TenantId { get; }

    /// <summary>Nome físico da base — preenchido SOMENTE quando autorizado.</summary>
    string? NomeBaseFisica { get; }

    /// <summary>Referência ao segredo de conexão no cofre — preenchida SOMENTE quando autorizado.</summary>
    string? ReferenciaSegredoConexao { get; }

    bool EstaAutorizado { get; }
}

/// <summary>Face mutável do contexto, manipulada apenas pelo pipeline de resolução.</summary>
public interface IContextoTenantMutavel : ITenantContext
{
    void Reiniciar();

    void MarcarResolvido(string principalId, Guid tenantId);

    void MarcarAutorizado(string principalId, TenantMetadados metadados);

    void MarcarInvalido(string? principalId);
}
