using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Administracao;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Segredos;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// P71-R1 (GAP-P71R1-02): prova contra MySQL REAL do isolamento das fontes administrativas que vivem no
/// CATÁLOGO COMPARTILHADO — onde o isolamento depende só do filtro <c>TenantId</c> da consulta (não há banco
/// por tenant): a listagem/detalhe de usuários (<see cref="IConsultaUsuariosAdmin"/>, P72).
/// Até aqui só havia testes com dobras. Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class AdministracaoCatalogoIsolamentoMySqlTests
{
    private const string Senha = "SenhaForte#2026";

    private readonly FixtureMultiTenant _fixture;

    public AdministracaoCatalogoIsolamentoMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Listagem_De_Usuarios_Do_Tenant_A_Nao_Inclui_Usuario_Nem_Papel_Do_Tenant_B()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var c = await SemearUsuariosAsync();

        using var escopo = _fixture.CriarEscopo();
        var consulta = escopo.ServiceProvider.GetRequiredService<IConsultaUsuariosAdmin>();

        var pagina = await consulta.ListarPorTenantAsync(c.A, new FiltroUsuariosAdmin { TamanhoPagina = 100 }.Normalizado());

        pagina.TotalItens.Should().Be(2);
        pagina.Itens.Select(u => u.UsuarioId).Should().BeEquivalentTo([c.SoA, c.Ambos]);
        pagina.Itens.Should().NotContain(u => u.UsuarioId == c.SoB);
        // O usuário vinculado aos dois tenants aparece com o papel do vínculo de A — nunca o papel que tem em B.
        pagina.Itens.Single(u => u.UsuarioId == c.Ambos).Papeis.Should().Equal(nameof(PapelConhecido.Leitor));
    }

    [SkippableFact]
    public async Task Busca_Pelo_Login_De_Usuario_De_B_Nao_Encontra_Nada_No_Tenant_A()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var c = await SemearUsuariosAsync();

        using var escopo = _fixture.CriarEscopo();
        var consulta = escopo.ServiceProvider.GetRequiredService<IConsultaUsuariosAdmin>();

        var deB = await consulta.ListarPorTenantAsync(c.A, new FiltroUsuariosAdmin { Busca = c.LoginSoB }.Normalizado());
        deB.TotalItens.Should().Be(0);
        deB.Itens.Should().BeEmpty();

        var deA = await consulta.ListarPorTenantAsync(c.A, new FiltroUsuariosAdmin { Busca = c.LoginSoA }.Normalizado());
        deA.Itens.Should().ContainSingle().Which.UsuarioId.Should().Be(c.SoA);
    }

    [SkippableFact]
    public async Task Detalhe_Por_Id_E_Escopado_Ao_Tenant()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var c = await SemearUsuariosAsync();

        using var escopo = _fixture.CriarEscopo();
        var consulta = escopo.ServiceProvider.GetRequiredService<IConsultaUsuariosAdmin>();

        (await consulta.ObterDoTenantAsync(c.A, c.SoB)).Should().BeNull("usuário de B consultado por Id no contexto de A");
        (await consulta.ObterDoTenantAsync(c.A, c.Ambos))!.Papeis.Should().Equal(nameof(PapelConhecido.Leitor));
        (await consulta.ObterDoTenantAsync(c.B, c.Ambos))!.Papeis.Should().Equal(nameof(PapelConhecido.Administrador));
    }

    [SkippableFact]
    public async Task Paginacao_E_Feita_No_Banco_Sem_Sobreposicao()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var c = await SemearUsuariosAsync();
        var (extra, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, extra, c.A, PapelConhecido.Analista);

        using var escopo = _fixture.CriarEscopo();
        var consulta = escopo.ServiceProvider.GetRequiredService<IConsultaUsuariosAdmin>();

        var p1 = await consulta.ListarPorTenantAsync(c.A, new FiltroUsuariosAdmin { Pagina = 1, TamanhoPagina = 2 }.Normalizado());
        var p2 = await consulta.ListarPorTenantAsync(c.A, new FiltroUsuariosAdmin { Pagina = 2, TamanhoPagina = 2 }.Normalizado());

        p1.TotalItens.Should().Be(3);
        p1.Itens.Should().HaveCount(2);
        p2.Itens.Should().ContainSingle();
        p1.Itens.Concat(p2.Itens).Select(u => u.UsuarioId).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo([c.SoA, c.Ambos, extra]);
    }

    private async Task<CenarioUsuarios> SemearUsuariosAsync()
    {
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (soA, loginSoA) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        var (soB, loginSoB) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);
        var (ambos, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, Senha);

        await CenarioIdentidade.ConcederPapelAsync(_fixture, soA, a, PapelConhecido.Analista);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, soB, b, PapelConhecido.Analista);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, ambos, a, PapelConhecido.Leitor);
        await CenarioIdentidade.ConcederPapelAsync(_fixture, ambos, b, PapelConhecido.Administrador);

        return new CenarioUsuarios(a, b, soA, loginSoA, soB, loginSoB, ambos);
    }

    private sealed record CenarioUsuarios(Guid A, Guid B, Guid SoA, string LoginSoA, Guid SoB, string LoginSoB, Guid Ambos);
}
