using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PortalBase.Integration.Tests.Hosts;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Hardening do host Web (Prompt 07): cabeçalhos de segurança presentes, sem cabeçalho Server,
/// health sem detalhes internos e antiforgery bloqueando POST sem token. Sobe o host no ambiente
/// "Testing" (não exige banco para a liveness).
/// </summary>
[Collection(ColecaoHostWeb.Nome)]
public sealed class SegurancaWebTests : IClassFixture<SegurancaWebTests.Fabrica>
{
    private readonly Fabrica _fabrica;

    public SegurancaWebTests(Fabrica fabrica) => _fabrica = fabrica;

    [Fact] // §5 — cabeçalhos de segurança presentes
    public async Task Cabecalhos_De_Seguranca_Presentes()
    {
        using var cliente = _fabrica.CreateClient();

        using var resposta = await cliente.GetAsync(new Uri("/health/live", UriKind.Relative));

        resposta.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        resposta.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
        resposta.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        resposta.Headers.Contains("Content-Security-Policy").Should().BeTrue();
        resposta.Headers.GetValues("Content-Security-Policy").Should().Contain(csp => csp.Contains("frame-ancestors 'none'"));
    }

    [Fact] // §24 — não expor o cabeçalho Server
    public async Task Sem_Cabecalho_Server()
    {
        using var cliente = _fabrica.CreateClient();

        using var resposta = await cliente.GetAsync(new Uri("/health/live", UriKind.Relative));

        resposta.Headers.Contains("Server").Should().BeFalse();
    }

    [Fact] // §25 — health não vaza detalhes internos (só o status)
    public async Task Health_Nao_Vaza_Detalhes()
    {
        using var cliente = _fabrica.CreateClient();

        using var resposta = await cliente.GetAsync(new Uri("/health/live", UriKind.Relative));
        var corpo = await resposta.Content.ReadAsStringAsync();

        corpo.Should().Contain("status");
        corpo.Should().NotContainAny("Server=", "password", "Exception", "connection", "at PortalBase");
    }

    [Fact] // §11 — POST state-changing sem antiforgery é rejeitado
    public async Task Login_Sem_Antiforgery_E_Rejeitado()
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resposta = await cliente.PostAsync(
            new Uri("/conta/login", UriKind.Relative),
            JsonContent.Create(new { login = "x", senha = "y" }));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest); // antiforgery ausente
        (await resposta.Content.ReadAsStringAsync()).Should().Contain("Solicitação inválida", "o POST recusado explica em português");
    }

    [Fact] // §16/§28 — endereço inexistente: página em português, sem detalhe interno
    public async Task Rota_Inexistente_Mostra_Pagina_Nao_Encontrada_Em_Portugues()
    {
        using var cliente = _fabrica.CreateClient();

        using var resposta = await cliente.GetAsync(new Uri("/rota-que-nao-existe-" + Guid.NewGuid().ToString("N"), UriKind.Relative));
        var corpo = WebUtility.HtmlDecode(await resposta.Content.ReadAsStringAsync()); // o Razor codifica acentos

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        corpo.Should().Contain("Página não encontrada").And.Contain("Voltar ao início");
        corpo.Should().NotContainAny("Exception", "at PortalBase", "Development");
    }

    [Theory] // Mensagens das respostas de erro de formulário (POST): código real + texto de negócio
    [InlineData(429, "Muitas tentativas")]
    [InlineData(400, "Solicitação inválida")]
    [InlineData(403, "Sem permissão")]
    [InlineData(404, "Página não encontrada")]
    [InlineData(413, "Arquivo grande demais")]
    [InlineData(500, "Não foi possível concluir")]
    public void Respostas_De_Erro_De_Formulario_Tem_Mensagem_De_Negocio(int status, string titulo)
        => PortalBase.Web.Seguranca.RespostaErroFormulario.Mensagem(status).Titulo.Should().Be(titulo);

    [Theory] // Sair sem sessão (cookie ausente/encerrado pelo servidor): nada a proteger — limpa e redireciona, sem 400.
    [InlineData(null, "/")]
    [InlineData("mfa-cadastrado", "/conta/entrar?mfa=cadastrado")]
    [InlineData("https://malicioso.exemplo", "/")]
    public async Task Logout_Sem_Sessao_So_Redireciona_Para_Destino_Fixo(string? aviso, string destino)
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var formulario = new FormUrlEncodedContent(aviso is null ? [] : [new KeyValuePair<string, string>("aviso", aviso)]);

        using var resposta = await cliente.PostAsync(new Uri("/conta/logout", UriKind.Relative), formulario);

        resposta.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resposta.Headers.Location!.OriginalString.Should().Be(destino, "o destino nunca vem do formulário");
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
