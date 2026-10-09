using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Infrastructure.Persistencia;
using PortalBase.Infrastructure.Persistencia.BaseTenant;

namespace PortalBase.Infrastructure.Multitenancy;

/// <summary>
/// Cria o <see cref="PortalBaseTenantDbContext"/> da base de um contribuinte (seção 9).
/// </summary>
public interface IFabricaContextoTenant
{
    /// <exception cref="TenantContextAusenteException">Contexto não autorizado (fail-closed).</exception>
    ValueTask<PortalBaseTenantDbContext> CriarAsync(ITenantContext contexto, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fábrica FAIL-CLOSED do contexto do tenant: exige um <see cref="ITenantContext"/> AUTORIZADO,
/// monta a conexão internamente (nunca do caller) com a credencial de RUNTIME de menor
/// privilégio, e usa o nome de base resolvido do catálogo. Sem tenant autorizado, não abre
/// (seções 8, 9, 10). Nenhum componente escolhe livremente o banco. O descarte do contexto aguarda as
/// operações em voo (<see cref="ProtecaoOperacaoEmVoo"/>, P62-R1).
/// </summary>
public sealed class FabricaContextoTenant : IFabricaContextoTenant
{
    private readonly ConstrutorConexaoTenant _construtor;
    private readonly ProtecaoOperacaoEmVoo _protecao;

    public FabricaContextoTenant(ConstrutorConexaoTenant construtor, ProtecaoOperacaoEmVoo protecao)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        ArgumentNullException.ThrowIfNull(protecao);
        _construtor = construtor;
        _protecao = protecao;
    }

    public async ValueTask<PortalBaseTenantDbContext> CriarAsync(
        ITenantContext contexto,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        // Fail-closed: só um contexto AUTORIZADO expõe o nome da base.
        if (!contexto.EstaAutorizado || string.IsNullOrWhiteSpace(contexto.NomeBaseFisica))
        {
            throw new TenantContextAusenteException();
        }

        var conexao = await _construtor
            .ConstruirRuntimeAsync(contexto.NomeBaseFisica, cancellationToken)
            .ConfigureAwait(false);

        var opcoes = new DbContextOptionsBuilder<PortalBaseTenantDbContext>()
            .UseMySQL(conexao)
            .AddInterceptors(_protecao)
            .Options;

        return new PortalBaseTenantDbContext(opcoes);
    }
}
