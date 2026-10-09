using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PortalBase.Integration.Tests.Hosts;

namespace PortalBase.Integration.Tests.Observabilidade;

/// <summary>
/// Prompt 51 — CORRELAÇÃO ponta a ponta no host Web (§3/§7). O <c>X-Correlation-ID</c> informado pelo
/// cliente é PRESERVADO (nunca regenerado por camada) e devolvido na resposta; quando ausente, o servidor
/// gera um. Confirma também que a resposta de health não vaza detalhes internos (§17). Ambiente "Testing".
/// </summary>
[Collection(ColecaoHostWeb.Nome)]
public sealed class CorrelacaoWebP51Tests : IClassFixture<CorrelacaoWebP51Tests.Fabrica>
{
    private const string Cabecalho = "X-Correlation-ID";
    private readonly Fabrica _fabrica;

    public CorrelacaoWebP51Tests(Fabrica fabrica) => _fabrica = fabrica;

    [Fact] // §3/§7 — o correlation id do cliente é preservado (não regenerado)
    public async Task Correlation_Do_Cliente_E_Preservado()
    {
        using var cliente = _fabrica.CreateClient();
        var correlation = "corr-" + Guid.NewGuid().ToString("N");

        using var requisicao = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        requisicao.Headers.TryAddWithoutValidation(Cabecalho, correlation);
        using var resposta = await cliente.SendAsync(requisicao);

        resposta.Headers.GetValues(Cabecalho).Should().ContainSingle().Which.Should().Be(correlation);
    }

    [Fact] // §3 — sem correlation id do cliente, o servidor gera um (não vazio)
    public async Task Correlation_Ausente_E_Gerado()
    {
        using var cliente = _fabrica.CreateClient();

        using var resposta = await cliente.GetAsync(new Uri("/health/live", UriKind.Relative));

        resposta.Headers.Contains(Cabecalho).Should().BeTrue();
        resposta.Headers.GetValues(Cabecalho).Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
    }

    [Fact] // §17 — a resposta pública de health é mínima (só status; sem connection string/host/segredo)
    public async Task Health_Publico_E_Minimo()
    {
        using var cliente = _fabrica.CreateClient();

        using var resposta = await cliente.GetAsync(new Uri("/health/live", UriKind.Relative));
        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var corpo = await resposta.Content.ReadAsStringAsync();

        corpo.Should().Contain("status");
        corpo.Should().NotContainAny("Server=", "Database=", "password", "Bearer", "connectionstring", "at PortalBase");
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
