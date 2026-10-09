using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Catalogo;

/// <summary>Porta de persistência do agregado <see cref="Tenant"/> (catálogo central).</summary>
public interface IRepositorioTenant
{
    ValueTask<bool> ExisteComCnpjAsync(Cnpj cnpj, CancellationToken cancellationToken = default);

    /// <summary>Já existe tenant com este nome (o nome define a base física — é único).</summary>
    ValueTask<bool> ExisteComNomeAsync(NomeTenant nome, CancellationToken cancellationToken = default);

    /// <summary>Obtém um tenant rastreado (para atualização de estado).</summary>
    ValueTask<Tenant?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask AdicionarAsync(Tenant tenant, CancellationToken cancellationToken = default);

    /// <summary>Ids dos tenants elegíveis a provisionamento/migração (todos, exceto desabilitados).</summary>
    ValueTask<IReadOnlyList<Guid>> ListarProvisionaveisAsync(CancellationToken cancellationToken = default);
}
