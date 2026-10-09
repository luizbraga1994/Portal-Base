using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PortalBase.Integration.Tests.Hosts;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// P62-R1 — prova de IMPOSIÇÃO do rate limiting no host Web real (pipeline completo), complementar a
/// <c>ConfiguracaoSegurancaP68Tests</c> (vinculação/validação das opções). Usa o endpoint de step-up MFA com um
/// token antiforgery REAL (lido do formulário renderizado pelo servidor): dentro do teto o HANDLER responde
/// (sem sessão ⇒ redirect para /conta/entrar); acima do teto a requisição é cortada ANTES do handler.
/// Garante também que o teto mais alto configurado no harness E2E integrado não desliga o limitador.
///
/// O achado OBS-P62R1-01 (a rejeição chegava como 400, porque o re-execute das páginas de status repetia o POST contra
/// uma página Blazor que exige antiforgery) foi resolvido por <c>RespostaErroFormulario</c>: para POST, o código REAL
/// (429) volta ao cliente, com uma página curta em português.
/// </summary>
[Collection(ColecaoHostWeb.Nome)]
public sealed partial class RateLimitWebTests : IClassFixture<RateLimitWebTests.Fabrica>
{
    private const int Teto = 2;
    private readonly Fabrica _fabrica;

    public RateLimitWebTests(Fabrica fabrica) => _fabrica = fabrica;

    [Fact]
    public async Task Mfa_Acima_Do_Teto_Da_Janela_E_Rejeitada_Antes_Do_Handler()
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var token = await ObterTokenAntiforgeryAsync(cliente);

        // Dentro do teto: o HANDLER é alcançado (antiforgery válido, sem sessão ⇒ redirect para o login).
        for (var i = 0; i < Teto; i++)
        {
            using var permitido = await PostarMfaAsync(cliente, token);
            permitido.StatusCode.Should().Be(HttpStatusCode.Redirect, $"a requisição {i + 1} está dentro do teto");
            permitido.Headers.Location?.OriginalString.Should().Be("/conta/entrar");
        }

        // Acima do teto: o limitador corta a requisição ANTES do handler (nenhum redirect do endpoint).
        using var bloqueado = await PostarMfaAsync(cliente, token);
        bloqueado.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            $"a requisição acima do teto não pode chegar ao handler (status observado: {(int)bloqueado.StatusCode})");
        bloqueado.Headers.Location.Should().BeNull();
        var corpo = await bloqueado.Content.ReadAsStringAsync();
        corpo.Should().Contain("Muitas tentativas").And.Contain("Aguarde alguns minutos");
        corpo.Should().NotContainAny("Exception", "at PortalBase", "antiforgery");
    }

    private static async Task<string> ObterTokenAntiforgeryAsync(HttpClient cliente)
    {
        using var pagina = await cliente.GetAsync(new Uri("/conta/entrar", UriKind.Relative));
        pagina.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await pagina.Content.ReadAsStringAsync();
        var achado = RegexToken().Match(html);
        achado.Success.Should().BeTrue("o formulário de login renderiza o token antiforgery");
        return WebUtility.HtmlDecode(achado.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> PostarMfaAsync(HttpClient cliente, string token) =>
        cliente.PostAsync(
            new Uri("/conta/mfa", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__RequestVerificationToken"] = token,
                ["codigo"] = "000000",
            }));

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex RegexToken();

    public sealed class Fabrica : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Testing");

            // UseSetting: visível no REGISTRO dos serviços (AddSegurancaIdentidade lê a seção no startup).
            builder.UseSetting("Seguranca:RateLimit:MfaPorJanela", Teto.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
