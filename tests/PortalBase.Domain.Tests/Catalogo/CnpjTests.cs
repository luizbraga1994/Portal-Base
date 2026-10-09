using FluentAssertions;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Tests.Catalogo;

public sealed class CnpjTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")]
    [InlineData("1144477700016")]     // 13 dígitos
    [InlineData("114447770001611")]   // 15 dígitos
    public void Criar_ComTamanhoOuVazioInvalido_Lanca(string? entrada)
    {
        var acao = () => Cnpj.Criar(entrada);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Criar_ComDigitosVerificadoresInvalidos_Lanca()
    {
        var acao = () => Cnpj.Criar("11444777000160");

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Criar_ComTodosOsDigitosIguais_Lanca()
    {
        var acao = () => Cnpj.Criar("11111111111111");

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Criar_ComMascara_RemoveNaoDigitosEValida()
    {
        Cnpj.Criar("11.444.777/0001-61").Valor.Should().Be("11444777000161");
    }

    [Fact]
    public void Criar_ComMesmoValor_SaoIguais()
    {
        Cnpj.Criar("11444777000161").Should().Be(Cnpj.Criar("11444777000161"));
    }
}
