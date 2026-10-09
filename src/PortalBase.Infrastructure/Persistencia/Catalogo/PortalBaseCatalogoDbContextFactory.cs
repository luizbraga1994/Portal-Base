using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PortalBase.Infrastructure.Persistencia.Catalogo;

/// <summary>
/// Fábrica de design-time do catálogo (para <c>dotnet ef</c>). Constrói o modelo sem
/// abrir conexão em runtime; a string é um placeholder não-conectável e sem
/// credenciais (regra 7), sobreponível por variável de ambiente.
/// </summary>
public sealed class PortalBaseCatalogoDbContextFactory : IDesignTimeDbContextFactory<PortalBaseCatalogoDbContext>
{
    public PortalBaseCatalogoDbContext CreateDbContext(string[] args)
    {
        var conexao = Environment.GetEnvironmentVariable("PORTALBASE_DESIGNTIME_CONNECTION")
                      ?? "server=localhost;database=portalbase_catalogo_designtime";

        var opcoes = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>()
            .UseMySQL(conexao)
            .Options;

        return new PortalBaseCatalogoDbContext(opcoes);
    }
}
