using FluentAssertions;
using PortalBase.Application.Idempotencia;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Tests.Idempotencia;

public sealed class IdempotencyKeyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Criar_ComVazio_Lanca(string? entrada)
    {
        var acao = () => IdempotencyKey.Criar(entrada);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Criar_AcimaDoMaximo_Lanca()
    {
        var entrada = new string('a', IdempotencyKey.TamanhoMaximo + 1);
        var acao = () => IdempotencyKey.Criar(entrada);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Criar_Valido_NormalizaEEquivale()
    {
        IdempotencyKey.Criar("  chave-1  ").Valor.Should().Be("chave-1");
        IdempotencyKey.Criar("chave-1").Should().Be(IdempotencyKey.Criar("chave-1"));
    }
}
