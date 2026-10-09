using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PortalBase.Integration.Tests.Hosts;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Login em duas etapas — a SELEÇÃO de contribuinte (seção 13/14). Garante o comportamento fail-closed
/// da superfície que roda sem banco: a página de seleção é servida e, sem sessão, é fail-closed (não
/// vaza nem lista contribuinte de ninguém); o POST de seleção exige antiforgery (§24). A prova positiva
/// de seleção com vínculos semeados exige MySQL (BLOCKED — coberta pela hidratação unitária à parte).
/// </summary>
[Collection(ColecaoHostWeb.Nome)]
public sealed class SelecaoContribuinteWebTests : IClassFixture<SelecaoContribuinteWebTests.Fabrica>
{
    private readonly Fabrica _fabrica;

    public SelecaoContribuinteWebTests(Fabrica fabrica) => _fabrica = fabrica;

    [Fact] // §24 — POST de seleção sem antiforgery é rejeitado (state-changing)
    public async Task Selecionar_Sem_Antiforgery_E_Rejeitado()
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resposta = await cliente.PostAsync(
            new Uri("/conta/contribuinte", UriKind.Relative),
            JsonContent.Create(new { tenantId = Guid.NewGuid().ToString() }));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact] // §41 — sem sessão, a seleção é fail-closed: nenhum contribuinte é listado nem detalhe vazado
    public async Task Selecao_Anonima_E_FailClosed_Sem_Vazamento()
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resposta = await cliente.GetAsync(new Uri("/conta/selecionar", UriKind.Relative));
        var corpo = await resposta.Content.ReadAsStringAsync();

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        corpo.Should().Contain("não está vinculado"); // estado seguro, sem listar tenant de ninguém
        corpo.Should().NotContainAny("Exception", "at PortalBase", "connectionstring", "password");
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
