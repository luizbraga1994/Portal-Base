using FluentAssertions;
using PortalBase.Application.Administracao;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Administracao;

/// <summary>
/// Edição dos dados cadastrais do contribuinte pela tela: só o contribuinte do contexto (servidor), com admin.tenant;
/// CNPJ e nome não mudam; sem mudança nada é gravado; edição concorrente é recusada; auditoria com antes/depois só dos
/// campos alterados.
/// </summary>
public sealed class EdicaoCadastroTenantTests
{
    private static readonly DateTimeOffset Criado = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Agora = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Atualiza_Grava_E_Audita_Antes_E_Depois_Dos_Campos_Alterados()
    {
        var c = Cenario.Novo();

        var r = await c.Comando.AtualizarAsync(new DadosCadastroTenant("Acme Comércio Ltda"), Criado);

        r.Should().Be(ResultadoEdicaoCadastroTenant.Gravado);
        c.Tenant.RazaoSocial.Should().Be("Acme Comércio Ltda");
        c.Tenant.Cnpj.Valor.Should().Be("11222333000181");
        c.Uow.Salvamentos.Should().Be(1);

        var evento = c.Auditoria.Eventos.Should().ContainSingle().Subject;
        evento.Operacao.Should().Be(AcaoAuditoria.TenantDetailsUpdated);
        evento.Tenant.Should().Be(c.Tenant.Id.ToString());
        evento.PrincipalId.Should().Be("admin-1");
        evento.ValoresAnteriores.Should().BeEquivalentTo(new Dictionary<string, string?> { ["razaoSocial"] = "Acme Ltda" });
        evento.ValoresNovos.Should().BeEquivalentTo(new Dictionary<string, string?> { ["razaoSocial"] = "Acme Comércio Ltda" });
    }

    [Fact]
    public async Task Sem_Mudanca_Nao_Grava_Nem_Audita()
    {
        var c = Cenario.Novo();

        var r = await c.Comando.AtualizarAsync(new DadosCadastroTenant(" Acme Ltda "), Criado);

        r.Should().Be(ResultadoEdicaoCadastroTenant.SemMudanca);
        c.Uow.Salvamentos.Should().Be(0);
        c.Auditoria.Eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task Campo_Vazio_E_Recusado_Sem_Gravar()
    {
        var c = Cenario.Novo();

        var r = await c.Comando.AtualizarAsync(new DadosCadastroTenant(""), Criado);

        r.Sucesso.Should().BeFalse();
        r.Erro.Should().Contain("Preencha a razão social");
        c.Uow.Salvamentos.Should().Be(0);
        c.Tenant.RazaoSocial.Should().Be("Acme Ltda");
    }

    [Fact]
    public async Task Edicao_Concorrente_E_Recusada()
    {
        var c = Cenario.Novo();

        var r = await c.Comando.AtualizarAsync(new DadosCadastroTenant("Outra"), Criado.AddMinutes(-5));

        r.Sucesso.Should().BeFalse();
        r.Erro.Should().Contain("alterado por outra pessoa");
        c.Tenant.RazaoSocial.Should().Be("Acme Ltda");
        c.Uow.Salvamentos.Should().Be(0);
    }

    [Fact]
    public async Task Sem_Permissao_Nega()
    {
        var c = Cenario.Novo();
        c.Autorizacao.Permitir = false;

        var acao = () => c.Comando.AtualizarAsync(new DadosCadastroTenant("Outra"), Criado);

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        c.Tenant.RazaoSocial.Should().Be("Acme Ltda");
    }

    [Fact]
    public async Task Sem_Contribuinte_Autorizado_Falha_Fechado()
    {
        var c = Cenario.Novo();
        c.Contexto.Estado = EstadoTenant.NaoResolvido;

        var acao = () => c.Comando.AtualizarAsync(new DadosCadastroTenant("Outra"), Criado);

        await acao.Should().ThrowAsync<TenantContextAusenteException>();
    }

    private sealed record Cenario(
        Tenant Tenant, ContextoTenantFake Contexto, ServicoAutorizacaoFake Autorizacao, RepositorioTenantFake Repositorio,
        UnitOfWorkFake Uow, AuditoriaFake Auditoria)
    {
        public EdicaoCadastroTenant Comando => new(
            Contexto, Autorizacao, new ContextoSegurancaFake { PrincipalId = "admin-1" }, Repositorio, Uow, Auditoria, new RelogioFixo(Agora));

        public static Cenario Novo()
        {
            var tenant = Tenant.Criar(Cnpj.Criar("11222333000181"), NomeTenant.Criar("ACME"), "Acme Ltda", "cofre://x", Criado);
            var repositorio = new RepositorioTenantFake();
            repositorio.Adicionados.Add(tenant);
            return new Cenario(tenant, new ContextoTenantFake { TenantId = tenant.Id }, new ServicoAutorizacaoFake(), repositorio, new UnitOfWorkFake(), new AuditoriaFake());
        }
    }
}
