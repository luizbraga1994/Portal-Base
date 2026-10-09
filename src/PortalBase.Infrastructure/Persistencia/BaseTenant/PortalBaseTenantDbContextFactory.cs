using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PortalBase.Infrastructure.Persistencia.BaseTenant;

/// <summary>
/// Fábrica de design-time da base do tenant, usada apenas para GERAR as migrations do
/// schema do tenant (que serão aplicadas independentemente a cada contribuinte no
/// Prompt 03). Não abre conexão em runtime; string placeholder sem credenciais (regra 7).
/// </summary>
public sealed class PortalBaseTenantDbContextFactory : IDesignTimeDbContextFactory<PortalBaseTenantDbContext>
{
    public PortalBaseTenantDbContext CreateDbContext(string[] args)
    {
        var conexao = Environment.GetEnvironmentVariable("PORTALBASE_TENANT_DESIGNTIME_CONNECTION")
                      ?? "server=localhost;database=portalbase_tenant_designtime";

        var opcoes = new DbContextOptionsBuilder<PortalBaseTenantDbContext>()
            .UseMySQL(conexao)
            .Options;

        return new PortalBaseTenantDbContext(opcoes);
    }
}
