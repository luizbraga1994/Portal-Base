using FluentAssertions;
using PortalBase.Domain.Comum;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Tests.Comum;

public sealed class DinheiroTests
{
    [Fact]
    public void Construtor_ComMaisDeDuasCasas_LancaEntradaInvalida()
    {
        var acao = () => new Dinheiro(10.123m);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Construtor_AcimaDaPrecisao_LancaEntradaInvalida()
    {
        var acao = () => new Dinheiro(100_000_000_000_000_000m);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Somar_DoisValores_RetornaSoma()
    {
        var resultado = new Dinheiro(10.50m).Somar(new Dinheiro(0.25m));

        resultado.Valor.Should().Be(10.75m);
    }

    [Fact]
    public void Subtrair_PodeResultarNegativo()
    {
        var resultado = new Dinheiro(1m).Subtrair(new Dinheiro(3m));

        resultado.EhNegativo.Should().BeTrue();
        resultado.Valor.Should().Be(-2m);
    }

    [Fact]
    public void Zero_EhValorPadrao()
    {
        Dinheiro.Zero.Valor.Should().Be(0m);
        default(Dinheiro).Should().Be(Dinheiro.Zero);
    }

    [Fact]
    public void ToString_FormataComDuasCasasInvariante()
    {
        new Dinheiro(1234.5m).ToString().Should().Be("1234.50");
    }
}
