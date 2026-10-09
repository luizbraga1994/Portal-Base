using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PortalBase.Integration.Tests.Hosts;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Segurança dos endpoints da MARCA VISUAL (logo). Os uploads são protegidos: exigem sessão (a
/// autorização é aplicada antes do handler) e, com sessão, antiforgery (§24). O serviço da logo do
/// CONTRIBUINTE também exige sessão (não é público). A prova positiva com bytes reais exige MySQL
/// (BLOCKED) — coberta pela validação de domínio (MarcaVisual) à parte.
/// </summary>
[Collection(ColecaoHostWeb.Nome)]
public sealed class MarcaWebTests : IClassFixture<MarcaWebTests.Fabrica>
{
    private readonly Fabrica _fabrica;

    public MarcaWebTests(Fabrica fabrica) => _fabrica = fabrica;

    [Fact] // upload da logo global sem sessão é negado pela autorização (nunca processa o arquivo)
    public async Task Upload_Plataforma_Anonimo_E_Negado()
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resposta = await cliente.PostAsync(
            new Uri("/admin/marca/plataforma", UriKind.Relative),
            JsonContent.Create(new { x = 1 }));

        ((int)resposta.StatusCode).Should().BeInRange(300, 499); // desafio de autenticação / negado
        resposta.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (resposta.Headers.Location?.OriginalString ?? string.Empty).Should().NotContain("ok="); // upload não ocorreu
    }

    [Fact] // upload da logo do contribuinte sem sessão é negado pela autorização
    public async Task Upload_Contribuinte_Anonimo_E_Negado()
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resposta = await cliente.PostAsync(
            new Uri("/admin/marca/contribuinte", UriKind.Relative),
            JsonContent.Create(new { x = 1 }));

        ((int)resposta.StatusCode).Should().BeInRange(300, 499);
        resposta.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (resposta.Headers.Location?.OriginalString ?? string.Empty).Should().NotContain("ok=");
    }

    [Fact] // a logo do CONTRIBUINTE não é pública: sem sessão nunca devolve a imagem
    public async Task Logo_Do_Contribuinte_Exige_Sessao()
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resposta = await cliente.GetAsync(new Uri("/branding/logo/contribuinte", UriKind.Relative));

        resposta.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    public sealed class Fabrica : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Testing");
        }
    }
}
