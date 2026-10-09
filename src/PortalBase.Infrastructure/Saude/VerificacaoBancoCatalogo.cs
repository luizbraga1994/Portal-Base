using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Saude;

/// <summary>
/// Readiness do banco de CATÁLOGO: verifica se a dependência crítica está acessível.
/// A resposta não expõe detalhes internos (regra 16) — apenas o estado.
/// </summary>
public sealed class VerificacaoBancoCatalogo : IHealthCheck
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public VerificacaoBancoCatalogo(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var acessivel = await _contexto.Database
                .CanConnectAsync(cancellationToken)
                .ConfigureAwait(false);

            return acessivel
                ? HealthCheckResult.Healthy("Banco de catálogo acessível.")
                : HealthCheckResult.Unhealthy("Banco de catálogo inacessível.");
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Banco de catálogo inacessível.");
        }
    }
}
