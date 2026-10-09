using FluentAssertions;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Tests.Catalogo;

public sealed class TenantTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static Tenant CriarValido(Guid? id = null)
        => Tenant.Criar(
            Cnpj.Criar("11444777000161"), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa X", "cofre://x", Momento, id);

    [Fact]
    public void Criar_ComecaPendente_ComIdEBaseFisica()
    {
        var tenant = CriarValido();

        tenant.Estado.Should().Be(EstadoProvisionamento.Pendente);
        tenant.EstaOperacional.Should().BeFalse();
        tenant.Id.Should().NotBe(Guid.Empty);
        tenant.NomeBaseFisica.Should().StartWith("portalbase_tenant_");
    }

    [Theory]
    [InlineData("STAKE", "STAKE", "portalbase_tenant_stake")]
    [InlineData("  stake ", "STAKE", "portalbase_tenant_stake")]
    [InlineData("Filial_02", "FILIAL_02", "portalbase_tenant_filial_02")]
    public void Nome_Escolhido_E_Maiusculo_E_Define_A_Base(string informado, string nome, string baseFisica)
    {
        var tenant = Tenant.Criar(
            Cnpj.Criar("11444777000161"), NomeTenant.Criar(informado), "Empresa X", "cofre://x", Momento);

        tenant.Nome.Should().Be(nome);
        tenant.NomeBaseFisica.Should().Be(baseFisica);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S")] // curto demais
    [InlineData("1STAKE")] // começa por dígito
    [InlineData("_STAKE")]
    [InlineData("STAKE LTDA")] // espaço
    [InlineData("AÇÃO")] // acento: não é identificador seguro de banco
    [InlineData("stakeı")] // 'ı' (i sem ponto) viraria 'I' ao pôr em maiúsculas
    [InlineData("ſtake")] // 's' longo viraria 'S'
    [InlineData("ＳＴＡＫＥ")] // letras de largura total
    [InlineData("STAKE-01")] // hífen
    [InlineData("stake`; DROP DATABASE x")] // tentativa de injeção no nome da base
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNO")] // 41 caracteres
    public void Nome_Invalido_E_Recusado(string? nome)
    {
        var criar = () => NomeTenant.Criar(nome);

        criar.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Nome_No_Limite_Cabe_No_Limite_Do_MySql()
    {
        var nome = NomeTenant.Criar(new string('A', NomeTenant.TamanhoMaximo));

        nome.NomeBaseFisica.Length.Should().BeLessThanOrEqualTo(64, "o MySQL limita o nome da base a 64 caracteres");
    }

    private static Tenant CriarPronto()
    {
        var tenant = CriarValido();
        tenant.IniciarProvisionamento(Momento);
        tenant.ConcluirProvisionamento("v", "v", Momento);
        return tenant;
    }

    [Fact]
    public void CicloDeProvisionamento_Feliz_TerminaPronto_MasNaoOperacional()
    {
        var tenant = CriarValido();
        var stamp = tenant.ConcurrencyStamp;

        tenant.IniciarProvisionamento(Momento);
        tenant.Estado.Should().Be(EstadoProvisionamento.Provisionando);

        tenant.ConcluirProvisionamento("20260902_X", "20260902_X", Momento.AddMinutes(1));
        // Provisionamento → PRONTO (Ready): infra validada, ainda NÃO operacional (Prompt 09).
        tenant.Estado.Should().Be(EstadoProvisionamento.Pronto);
        tenant.EstaOperacional.Should().BeFalse();
        tenant.EstaProvisionado.Should().BeTrue();
        tenant.SchemaVersion.Should().Be("20260902_X");
        tenant.UltimoErro.Should().BeNull();
        tenant.ConcurrencyStamp.Should().NotBe(stamp);
    }

    [Fact]
    public void Ativar_APartirDePronto_TornaOperacional()
    {
        var tenant = CriarPronto();

        tenant.Ativar(Momento.AddMinutes(1));

        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
        tenant.EstaOperacional.Should().BeTrue();
    }

    [Fact]
    public void Ativar_APartirDePendente_Lanca()
    {
        var tenant = CriarValido();

        var acao = () => tenant.Ativar(Momento);

        acao.Should().Throw<EstadoInvalidoException>();
    }

    [Fact]
    public void Ativar_JaAtivo_EhIdempotente()
    {
        var tenant = CriarPronto();
        tenant.Ativar(Momento);

        var acao = () => tenant.Ativar(Momento);

        acao.Should().NotThrow();
        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
    }

    [Fact]
    public void Reativar_APartirDeDesabilitado_TornaOperacional()
    {
        var tenant = CriarPronto();
        tenant.Ativar(Momento);
        tenant.Desabilitar(Momento.AddMinutes(1));

        tenant.Reativar(Momento.AddMinutes(2));

        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
        tenant.EstaOperacional.Should().BeTrue();
    }

    [Fact]
    public void Reativar_APartirDePronto_Lanca()
    {
        var tenant = CriarPronto();

        var acao = () => tenant.Reativar(Momento);

        acao.Should().Throw<EstadoInvalidoException>();
    }

    [Fact]
    public void FalharProvisionamento_MantemNaoOperacional_ERegistraErro()
    {
        var tenant = CriarValido();
        tenant.IniciarProvisionamento(Momento);

        tenant.FalharProvisionamento("InvalidOperationException: falha", Momento.AddMinutes(1));

        tenant.Estado.Should().Be(EstadoProvisionamento.Falhado);
        tenant.EstaOperacional.Should().BeFalse();
        tenant.UltimoErro.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void IniciarProvisionamento_APartirDePronto_Lanca()
    {
        var tenant = CriarValido();
        tenant.IniciarProvisionamento(Momento);
        tenant.ConcluirProvisionamento("v", "v", Momento);

        var acao = () => tenant.IniciarProvisionamento(Momento);

        acao.Should().Throw<EstadoInvalidoException>();
    }

    [Fact]
    public void Desabilitar_TornaNaoOperacional()
    {
        var tenant = CriarPronto();
        tenant.Ativar(Momento);

        tenant.Desabilitar(Momento.AddMinutes(1));

        tenant.Estado.Should().Be(EstadoProvisionamento.Desabilitado);
        tenant.EstaOperacional.Should().BeFalse();
    }

    [Fact]
    public void Reprovisionar_APartirDeFalhado_EhPermitido()
    {
        var tenant = CriarValido();
        tenant.IniciarProvisionamento(Momento);
        tenant.FalharProvisionamento("erro", Momento);

        var acao = () => tenant.IniciarProvisionamento(Momento.AddMinutes(1));

        acao.Should().NotThrow(); // Falhado → Provisionando (reprocesso)
        tenant.Estado.Should().Be(EstadoProvisionamento.Provisionando);
    }

    [Fact]
    public void Criar_ComRazaoSocialVazia_Lanca()
    {
        var acao = () => Tenant.Criar(
            Cnpj.Criar("11444777000161"), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "  ", "cofre://x", Momento);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void AtualizarCadastro_Muda_A_Razao_Social_E_Mantem_Cnpj_E_Nome()
    {
        var tenant = CriarValido();
        var (cnpj, nome, baseFisica, stamp) = (tenant.Cnpj, tenant.Nome, tenant.NomeBaseFisica, tenant.ConcurrencyStamp);

        var mudou = tenant.AtualizarCadastro("  Empresa Y Ltda  ", Momento.AddHours(1));

        mudou.Should().BeTrue();
        tenant.RazaoSocial.Should().Be("Empresa Y Ltda");
        tenant.AtualizadoEmUtc.Should().Be(Momento.AddHours(1));
        tenant.ConcurrencyStamp.Should().NotBe(stamp);
        (tenant.Cnpj, tenant.Nome, tenant.NomeBaseFisica).Should().Be((cnpj, nome, baseFisica), "CNPJ e nome identificam o contribuinte e a base");
    }

    [Fact]
    public void AtualizarCadastro_Sem_Mudanca_Nao_Toca_No_Agregado()
    {
        var tenant = CriarValido();
        var stamp = tenant.ConcurrencyStamp;

        tenant.AtualizarCadastro(" Empresa X ", Momento.AddHours(1)).Should().BeFalse();

        tenant.ConcurrencyStamp.Should().Be(stamp);
        tenant.AtualizadoEmUtc.Should().Be(Momento);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AtualizarCadastro_Razao_Vazia_E_Recusada_Sem_Alterar(string? razao)
    {
        var tenant = CriarValido();

        var acao = () => tenant.AtualizarCadastro(razao, Momento);

        acao.Should().Throw<EntradaInvalidaException>();
        tenant.RazaoSocial.Should().Be("Empresa X");
    }

    [Fact]
    public void AtualizarCadastro_Acima_Do_Limite_E_Recusado()
    {
        var tenant = CriarValido();

        var acao = () => tenant.AtualizarCadastro(new string('x', Tenant.TamanhoMaximoRazaoSocial + 1), Momento);

        acao.Should().Throw<EntradaInvalidaException>();
        tenant.AtualizarCadastro(new string('x', Tenant.TamanhoMaximoRazaoSocial), Momento).Should().BeTrue();
    }
}
