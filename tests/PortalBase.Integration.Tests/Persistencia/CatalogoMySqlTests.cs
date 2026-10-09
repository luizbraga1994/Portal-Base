using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;
using Testcontainers.MySql;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Catálogo em MySQL real (Testcontainers): migrations, seeds, constraints, persistência
/// e a unicidade de CNPJ garantida pelo BANCO (segunda barreira contra corrida).
/// Pulado automaticamente sem Docker; executa no CI.
/// </summary>
public sealed class CatalogoMySqlTests
{
    // CNPJ válido (dígitos verificadores corretos) para os testes.
    private const string CnpjValido = "11444777000161";

    [SkippableFact]
    public async Task Migrations_Seeds_E_Persistencia_De_Tenant()
    {
        Skip.IfNot(DockerDisponivel.Verificar(), "Docker indisponível neste ambiente.");

        await using var container = new MySqlBuilder("mysql:8.4").Build();
        await container.StartAsync();

        var opcoes = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>()
            .UseMySQL(container.GetConnectionString())
            .Options;

        await using var contexto = new PortalBaseCatalogoDbContext(opcoes);
        await contexto.Database.MigrateAsync();

        // Seeds idempotentes: UM papel e UMA permissão por valor do enum canônico. As contagens são DERIVADAS da
        // MESMA fonte que o seed itera (ConfiguracaoPapel/ConfiguracaoPermissao usam Enum.GetValues<...>), de modo
        // que nunca drifta ao adicionar um novo papel/permissão (hoje: 4 papéis, 21 permissões).
        (await contexto.Papeis.CountAsync()).Should().Be(Enum.GetValues<PapelConhecido>().Length);
        (await contexto.Permissoes.CountAsync()).Should().Be(Enum.GetValues<PermissaoConhecida>().Length);

        // Persistência e recuperação de um tenant (nome escolhido -> base portalbase_tenant_<nome>).
        var tenant = Tenant.Criar(
            Cnpj.Criar(CnpjValido), NomeTenant.Criar("stake"), "Empresa X", "cofre://x", DateTimeOffset.UtcNow);
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();

        var recuperado = await contexto.Tenants.AsNoTracking().FirstAsync();
        recuperado.Cnpj.Valor.Should().Be(CnpjValido);
        recuperado.Estado.Should().Be(EstadoProvisionamento.Pendente);
        recuperado.Nome.Should().Be("STAKE");
        recuperado.NomeBaseFisica.Should().Be("portalbase_tenant_stake");
    }

    [SkippableFact]
    public async Task Unicidade_Do_Nome_Do_Tenant_E_Garantida_Pelo_Banco()
    {
        Skip.IfNot(DockerDisponivel.Verificar(), "Docker indisponível neste ambiente.");

        await using var container = new MySqlBuilder("mysql:8.4").Build();
        await container.StartAsync();

        var opcoes = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>()
            .UseMySQL(container.GetConnectionString())
            .Options;

        await using var contexto = new PortalBaseCatalogoDbContext(opcoes);
        await contexto.Database.MigrateAsync();

        contexto.Tenants.Add(Tenant.Criar(
            Cnpj.Criar(CnpjValido), NomeTenant.Criar("STAKE"), "Empresa X", "cofre://x", DateTimeOffset.UtcNow));
        await contexto.SaveChangesAsync();

        // Outro CNPJ com o MESMO nome (a checagem da aplicação é a 1ª barreira; o índice ux_tenant_nome é a 2ª,
        // contra corrida): o banco deve rejeitar — senão dois contribuintes disputariam a base portalbase_tenant_stake.
        contexto.Tenants.Add(Tenant.Criar(
            Cnpj.Criar("11222333000181"), NomeTenant.Criar("stake"), "Empresa Y", "cofre://y", DateTimeOffset.UtcNow));

        var acao = async () => await contexto.SaveChangesAsync();
        await acao.Should().ThrowAsync<DbUpdateException>();
    }

    [SkippableFact]
    public async Task Unicidade_De_Cnpj_E_Garantida_Pelo_Banco()
    {
        Skip.IfNot(DockerDisponivel.Verificar(), "Docker indisponível neste ambiente.");

        await using var container = new MySqlBuilder("mysql:8.4").Build();
        await container.StartAsync();

        var opcoes = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>()
            .UseMySQL(container.GetConnectionString())
            .Options;

        await using var contexto = new PortalBaseCatalogoDbContext(opcoes);
        await contexto.Database.MigrateAsync();

        contexto.Tenants.Add(Tenant.Criar(
            Cnpj.Criar(CnpjValido), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa X", "cofre://x", DateTimeOffset.UtcNow));
        await contexto.SaveChangesAsync();

        // Segundo tenant com o MESMO CNPJ: a constraint única do banco deve rejeitar.
        contexto.Tenants.Add(Tenant.Criar(
            Cnpj.Criar(CnpjValido), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa Y", "cofre://y", DateTimeOffset.UtcNow));

        var acao = async () => await contexto.SaveChangesAsync();
        await acao.Should().ThrowAsync<DbUpdateException>();
    }
}
