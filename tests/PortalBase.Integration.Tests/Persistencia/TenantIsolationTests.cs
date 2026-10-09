using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Provisionamento;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Arquivos;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Prova de ISOLAMENTO entre contribuintes (seções 27-29, 37; CA-012) contra MySQL real.
/// Tenta deliberadamente quebrar o isolamento (IDOR, contexto ausente/inválido, tenant
/// desabilitado) e valida a separação física. Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class TenantIsolationTests
{
    private readonly FixtureMultiTenant _fixture;

    public TenantIsolationTests(FixtureMultiTenant fixture) => _fixture = fixture;

    // TESTE 1/2/9 — dado inserido em A não aparece em B; bases fisicamente distintas.
    [SkippableFact]
    public async Task Dado_De_A_Nao_Aparece_Em_B()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var a = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        a.Should().NotBe(b); // identificadores → nomes de base distintos (TESTE 9)

        var usuario = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, a);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, b);

        using (var escopo = _fixture.CriarEscopo())
        {
            await using var contabilA = await CenarioMultiTenant.AbrirContabilAsync(escopo, usuario, a);
            var agora = DateTimeOffset.UtcNow;
            contabilA.ArquivosImportacao.Add(ArquivoImportacao.Registrar(
                "planilha.csv", "seguro.csv", "csv", "text/csv", 42, new string('a', 64), Guid.NewGuid(), "corr-isolamento", 5, agora, agora.AddYears(5)));
            await contabilA.SaveChangesAsync();
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            await using var contabilA = await CenarioMultiTenant.AbrirContabilAsync(escopo, usuario, a);
            (await contabilA.ArquivosImportacao.CountAsync()).Should().Be(1); // achado em A
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            await using var contabilB = await CenarioMultiTenant.AbrirContabilAsync(escopo, usuario, b);
            (await contabilB.ArquivosImportacao.CountAsync()).Should().Be(0); // NÃO existe em B (isolamento físico)
        }
    }

    // TESTE 3 — IDOR: usuário autorizado só para A tenta B → negado.
    [SkippableFact]
    public async Task Usuario_De_A_Nao_Resolve_B()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var a = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var usuario = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, a); // só A

        using var escopo = _fixture.CriarEscopo();
        var resolvedor = escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>();

        var acao = async () => await resolvedor.ResolverAsync(usuario, b);

        await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
    }

    // TESTE 4 — resolução inválida não deixa o contexto autorizado e a fábrica recusa.
    [SkippableFact]
    public async Task Resolucao_Negada_Nao_Abre_Conexao_De_Tenant()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var a = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var usuarioSemVinculo = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);

        using var escopo = _fixture.CriarEscopo();
        var resolvedor = escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>();
        try
        {
            await resolvedor.ResolverAsync(usuarioSemVinculo, a);
        }
        catch (TenantNaoAutorizadoException)
        {
            // esperado
        }

        var contexto = escopo.ServiceProvider.GetRequiredService<ITenantContext>();
        contexto.EstaAutorizado.Should().BeFalse();

        var fabrica = escopo.ServiceProvider.GetRequiredService<IFabricaContextoTenant>();
        var acao = async () => await fabrica.CriarAsync(contexto);
        await acao.Should().ThrowAsync<TenantContextAusenteException>();
    }

    // TESTE 5 — fábrica sem contexto autorizado é rejeitada (fail-closed).
    [SkippableFact]
    public async Task Fabrica_Sem_Contexto_Autorizado_E_Rejeitada()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        using var escopo = _fixture.CriarEscopo();
        var fabrica = escopo.ServiceProvider.GetRequiredService<IFabricaContextoTenant>();

        var acao = async () => await fabrica.CriarAsync(new ContextoTenant());

        await acao.Should().ThrowAsync<TenantContextAusenteException>();
    }

    // TESTE 6 — tenant desabilitado bloqueia o acesso.
    [SkippableFact]
    public async Task Tenant_Desabilitado_E_Negado()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var a = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var usuario = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, a);
        await CenarioMultiTenant.DesabilitarTenantAsync(_fixture, a);

        using var escopo = _fixture.CriarEscopo();
        var resolvedor = escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>();

        var acao = async () => await resolvedor.ResolverAsync(usuario, a);

        await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
    }

    // TESTE 7 — dois provisionamentos concorrentes do mesmo tenant: sem corrupção; um efetivo.
    [SkippableFact]
    public async Task Provisionamento_Concorrente_Nao_Corrompe()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var id = await CriarTenantPendenteAsync(CenarioMultiTenant.GerarCnpjValido());

        async Task<ResultadoProvisionamento> Provisionar()
        {
            using var escopo = _fixture.CriarEscopo();
            var provisionador = escopo.ServiceProvider.GetRequiredService<IProvisionadorTenant>();
            return await provisionador.ProvisionarAsync(id, "sistema");
        }

        var resultados = await Task.WhenAll(Provisionar(), Provisionar());

        resultados.Should().OnlyContain(r =>
            r.Estado == EstadoProvisionamento.Pronto || r.Estado == EstadoProvisionamento.Provisionando);
        resultados.Count(r => r.Estado == EstadoProvisionamento.Pronto).Should().BeGreaterThanOrEqualTo(1);

        using var verificacao = _fixture.CriarEscopo();
        var repositorio = verificacao.ServiceProvider.GetRequiredService<IRepositorioTenant>();
        (await repositorio.ObterPorIdAsync(id))!.Estado.Should().Be(EstadoProvisionamento.Pronto);
    }

    // CA-012 (grants) — a credencial de RUNTIME não acessa o catálogo (grants por base, nunca *.*).
    [SkippableFact]
    public async Task Runtime_Nao_Acessa_O_Catalogo()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());

        using var escopo = _fixture.CriarEscopo();
        var construtor = escopo.ServiceProvider.GetRequiredService<ConstrutorConexaoTenant>();
        var conexaoRuntimeNoCatalogo = await construtor.ConstruirRuntimeAsync(_fixture.CatalogoDb);

        await using var conexao = new MySqlConnection(conexaoRuntimeNoCatalogo);
        var acao = async () => await conexao.OpenAsync();

        await acao.Should().ThrowAsync<MySqlException>(); // acesso negado ao catálogo
    }

    // TESTE 10 — identificadores de banco manipulados são rejeitados (não vão para DDL).
    [Fact]
    public void Nome_De_Base_Manipulado_E_Rejeitado()
    {
        var maliciosos = new[]
        {
            "tenant_a; DROP DATABASE x",
            "tenant_a`.`outro",
            "TENANT_MAIUSCULO",
            "../escape",
            "mysql",
        };

        foreach (var nome in maliciosos)
        {
            var acao = () => ConstrutorConexaoTenant.ValidarNomeBase(nome);
            acao.Should().Throw<EntradaInvalidaException>();
        }
    }

    private async Task<Guid> CriarTenantPendenteAsync(string cnpj)
    {
        Guid id;
        using var escopo = _fixture.CriarEscopo();
        var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioTenant>();
        var unitOfWork = escopo.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var tenant = Tenant.Criar(Cnpj.Criar(cnpj), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa", "cofre://x", DateTimeOffset.UtcNow);
        id = tenant.Id;
        await repositorio.AdicionarAsync(tenant);
        await unitOfWork.SalvarAlteracoesAsync();
        _fixture.RegistrarTenant(id);
        return id;
    }
}
