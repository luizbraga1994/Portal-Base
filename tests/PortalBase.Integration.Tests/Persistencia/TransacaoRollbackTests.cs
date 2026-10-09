using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;
using Testcontainers.MySql;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Valida que o rollback de transação NÃO persiste alterações (integridade sob falha).
/// </summary>
public sealed class TransacaoRollbackTests
{
    [SkippableFact]
    public async Task Rollback_Nao_Persiste_Alteracoes()
    {
        Skip.IfNot(DockerDisponivel.Verificar(), "Docker indisponível neste ambiente.");

        await using var container = new MySqlBuilder("mysql:8.4").Build();
        await container.StartAsync();

        var opcoes = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>()
            .UseMySQL(container.GetConnectionString())
            .Options;

        await using (var contexto = new PortalBaseCatalogoDbContext(opcoes))
        {
            await contexto.Database.MigrateAsync();

            await using var transacao = await contexto.Database.BeginTransactionAsync();
            contexto.Tenants.Add(Tenant.Criar(
                Cnpj.Criar("11444777000161"), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa X", "cofre://x", DateTimeOffset.UtcNow));
            await contexto.SaveChangesAsync();
            await transacao.RollbackAsync();
        }

        // Contexto novo: confirma que nada foi persistido.
        await using var verificacao = new PortalBaseCatalogoDbContext(opcoes);
        (await verificacao.Tenants.CountAsync()).Should().Be(0);
    }
}
