using FluentAssertions;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Shared.Resultados;

namespace PortalBase.Application.Tests.Catalogo;

public sealed class CriarTenantTests
{
    private const string CnpjValido = "11444777000161";
    private static readonly DateTimeOffset Momento = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static CriarTenantComando Comando(string cnpj = CnpjValido, string solicitante = "admin-1", string nome = "stake")
        => new(solicitante, cnpj, nome, "Empresa X", "cofre://x");

    private static CriarTenantManipulador Criar(
        RepositorioTenantFake repositorio,
        UnitOfWorkFake unitOfWork,
        out AuditoriaFake auditoria,
        AutorizacaoPlataformaFake? autorizacao = null)
    {
        auditoria = new AuditoriaFake();
        return new CriarTenantManipulador(
            autorizacao ?? new AutorizacaoPlataformaFake(),
            repositorio,
            unitOfWork,
            auditoria,
            new RelogioFixo(Momento));
    }

    [Fact]
    public async Task Sucesso_PersisteEAudita()
    {
        var repositorio = new RepositorioTenantFake(existe: false);
        var unitOfWork = new UnitOfWorkFake();
        var manipulador = Criar(repositorio, unitOfWork, out var auditoria);

        var resultado = await manipulador.ManipularAsync(Comando());

        resultado.Sucesso.Should().BeTrue();
        resultado.Valor.Should().NotBe(Guid.Empty);
        repositorio.Adicionados.Should().ContainSingle();
        unitOfWork.Salvamentos.Should().Be(1);
        auditoria.Eventos.Should().ContainSingle().Which.Operacao.Should().Be("TenantCriado");
    }

    [Fact]
    public async Task Nome_Escolhido_Vira_Maiusculo_E_Define_A_Base_Fisica()
    {
        var repositorio = new RepositorioTenantFake();
        var manipulador = Criar(repositorio, new UnitOfWorkFake(), out _);

        (await manipulador.ManipularAsync(Comando(nome: "  stake "))).Sucesso.Should().BeTrue();

        var tenant = repositorio.Adicionados.Should().ContainSingle().Subject;
        tenant.Nome.Should().Be("STAKE");
        tenant.NomeBaseFisica.Should().Be("portalbase_tenant_stake");
    }

    [Theory]
    [InlineData("")]
    [InlineData("S")]
    [InlineData("1STAKE")]
    [InlineData("STAKE LTDA")]
    [InlineData("AÇÃO")]
    [InlineData("stake;DROP DATABASE x")]
    [InlineData("stake`")]
    public async Task Nome_Invalido_E_Recusado_Sem_Persistir(string nome)
    {
        var repositorio = new RepositorioTenantFake();
        var unitOfWork = new UnitOfWorkFake();
        var manipulador = Criar(repositorio, unitOfWork, out _);

        var resultado = await manipulador.ManipularAsync(Comando(nome: nome));

        resultado.Falhou.Should().BeTrue();
        resultado.Erro!.Tipo.Should().Be(TipoErro.Validacao);
        repositorio.Adicionados.Should().BeEmpty();
        unitOfWork.Salvamentos.Should().Be(0);
    }

    [Fact]
    public async Task NomeJaExistente_RetornaConflito_SemPersistir()
    {
        var repositorio = new RepositorioTenantFake(existe: false, nomeExiste: true);
        var manipulador = Criar(repositorio, new UnitOfWorkFake(), out _);

        var resultado = await manipulador.ManipularAsync(Comando());

        resultado.Falhou.Should().BeTrue();
        resultado.Erro!.Tipo.Should().Be(TipoErro.Conflito);
        resultado.Erro.Codigo.Should().Be("tenant.nome_duplicado");
        repositorio.Adicionados.Should().BeEmpty();
    }

    [Fact]
    public async Task CnpjInvalido_RetornaFalhaDeValidacao()
    {
        var manipulador = Criar(new RepositorioTenantFake(), new UnitOfWorkFake(), out _);

        var resultado = await manipulador.ManipularAsync(Comando(cnpj: "123"));

        resultado.Falhou.Should().BeTrue();
        resultado.Erro!.Tipo.Should().Be(TipoErro.Validacao);
    }

    [Fact]
    public async Task CnpjJaExistente_RetornaConflito()
    {
        var manipulador = Criar(new RepositorioTenantFake(existe: true), new UnitOfWorkFake(), out _);

        var resultado = await manipulador.ManipularAsync(Comando());

        resultado.Falhou.Should().BeTrue();
        resultado.Erro!.Tipo.Should().Be(TipoErro.Conflito);
    }

    [Fact]
    public async Task CorridaNaConstraintUnica_RetornaConflito()
    {
        var unitOfWork = new UnitOfWorkFake { LancarUnicidade = true };
        var manipulador = Criar(new RepositorioTenantFake(existe: false), unitOfWork, out _);

        var resultado = await manipulador.ManipularAsync(Comando());

        resultado.Falhou.Should().BeTrue();
        resultado.Erro!.Tipo.Should().Be(TipoErro.Conflito);
    }

    [Fact]
    public async Task NaoAutenticado_RetornaNaoAutenticado_ENaoPersiste()
    {
        // Prompt 71 (P2-14): a autorização de plataforma é a barreira — não autenticado ⇒ falha, sem criar.
        var repositorio = new RepositorioTenantFake(existe: false);
        var unitOfWork = new UnitOfWorkFake();
        var autz = new AutorizacaoPlataformaFake { Permitir = false, Motivo = MotivoNegacao.NaoAutenticado };
        var manipulador = Criar(repositorio, unitOfWork, out var auditoria, autz);

        var resultado = await manipulador.ManipularAsync(Comando(solicitante: "  "));

        resultado.Falhou.Should().BeTrue();
        resultado.Erro!.Tipo.Should().Be(TipoErro.NaoAutenticado);
        repositorio.Adicionados.Should().BeEmpty();
        unitOfWork.Salvamentos.Should().Be(0);
        auditoria.Eventos.Should().ContainSingle().Which.Operacao.Should().Be("TenantCriacaoNegada");
    }

    [Fact]
    public async Task AutenticadoSemPermissaoDePlataforma_RetornaNaoAutorizado_ENaoPersiste()
    {
        // Usuário autenticado, mas NÃO administrador de plataforma: criar tenant é negado no Application
        // (não basta autorização de tenant/UI). Tentativa direta ao caso de uso é bloqueada e auditada.
        var repositorio = new RepositorioTenantFake(existe: false);
        var unitOfWork = new UnitOfWorkFake();
        var autz = new AutorizacaoPlataformaFake { Permitir = false, Motivo = MotivoNegacao.SemPermissao };
        var manipulador = Criar(repositorio, unitOfWork, out var auditoria, autz);

        var resultado = await manipulador.ManipularAsync(Comando());

        resultado.Falhou.Should().BeTrue();
        resultado.Erro!.Tipo.Should().Be(TipoErro.NaoAutorizado);
        repositorio.Adicionados.Should().BeEmpty();
        unitOfWork.Salvamentos.Should().Be(0);
        auditoria.Eventos.Should().ContainSingle().Which.Operacao.Should().Be("TenantCriacaoNegada");
    }
}
