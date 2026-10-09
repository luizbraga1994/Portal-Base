using FluentAssertions;
using PortalBase.Web.Seguranca;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>Proteção contra OPEN REDIRECT (Prompt 07, seção 14).</summary>
public sealed class RedirecionamentoSeguroTests
{
    [Theory]
    [InlineData("/painel", "/painel")]
    [InlineData("/conta/mfa?returnUrl=/x", "/conta/mfa?returnUrl=/x")]
    public void Destinos_Locais_Sao_Mantidos(string entrada, string esperado)
        => RedirecionamentoSeguro.Local(entrada).Should().Be(esperado);

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>")]
    [InlineData("")]
    [InlineData(null)]
    public void Destinos_Externos_Ou_Perigosos_Caem_No_Fallback(string? entrada)
        => RedirecionamentoSeguro.Local(entrada).Should().Be("/");
}
