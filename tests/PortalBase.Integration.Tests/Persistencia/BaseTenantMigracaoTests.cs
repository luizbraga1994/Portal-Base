using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PortalBase.Domain.Arquivos;
using PortalBase.Infrastructure.Persistencia.BaseTenant;
using PortalBase.Integration.Tests.Infra;
using Testcontainers.MySql;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Base individual do contribuinte em MySQL real: valida que as migrations do
/// PortalBaseTenantDbContext podem ser executadas independentemente e que o schema
/// do tenant persiste os metadados de arquivo (hash, tamanho, estado).
/// </summary>
public sealed class BaseTenantMigracaoTests
{
    [SkippableFact]
    public async Task Migrations_Do_Tenant_Aplicam_E_Persistem_Arquivo()
    {
        Skip.IfNot(DockerDisponivel.Verificar(), "Docker indisponível neste ambiente.");

        await using var container = new MySqlBuilder("mysql:8.4").Build();
        await container.StartAsync();

        var opcoes = new DbContextOptionsBuilder<PortalBaseTenantDbContext>()
            .UseMySQL(container.GetConnectionString())
            .Options;

        await using var contexto = new PortalBaseTenantDbContext(opcoes);
        await contexto.Database.MigrateAsync();

        var agora = DateTimeOffset.UtcNow;
        var arquivo = ArquivoImportacao.Registrar(
            "planilha.csv", "seguro.csv", "csv", "text/csv", 1234, new string('b', 64), Guid.NewGuid(), "corr-migracao", 5, agora, agora.AddYears(5));
        contexto.ArquivosImportacao.Add(arquivo);
        await contexto.SaveChangesAsync();

        var lido = await contexto.ArquivosImportacao.AsNoTracking().FirstAsync();
        lido.TamanhoBytes.Should().Be(1234);
        lido.Sha256.Should().Be(new string('b', 64));
    }
}
