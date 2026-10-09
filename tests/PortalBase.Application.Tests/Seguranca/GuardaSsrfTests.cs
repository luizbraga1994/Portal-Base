using System.Net;
using FluentAssertions;
using PortalBase.Application.Seguranca;

namespace PortalBase.Application.Tests.Seguranca;

/// <summary>Classificação de destinos para SSRF (Prompt 07, seção 18).</summary>
public sealed class GuardaSsrfTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // metadata endpoint
    [InlineData("100.64.0.1")]      // CGNAT
    [InlineData("0.0.0.0")]
    [InlineData("fc00::1")]         // ULA
    [InlineData("fe80::1")]         // link-local
    public void Ips_Internos_Sao_Bloqueados(string ip)
        => GuardaSsrf.IpBloqueado(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Ips_Publicos_Sao_Permitidos(string ip)
        => GuardaSsrf.IpBloqueado(IPAddress.Parse(ip)).Should().BeFalse();

    [Theory]
    [InlineData("http://exemplo.com", true)]
    [InlineData("https://exemplo.com", true)]
    [InlineData("ftp://exemplo.com", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("gopher://x", false)]
    public void Somente_Http_Https_Sao_Aceitos(string url, bool esperado)
        => GuardaSsrf.EsquemaPermitido(new Uri(url)).Should().Be(esperado);
}
