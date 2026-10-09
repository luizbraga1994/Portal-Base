using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PortalBase.Integration.Tests.Hosts;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Comportamento de apresentação fail-closed: um usuário NÃO autenticado que acessa uma rota
/// protegida (área do portal, sob o <c>MainLayout</c>) é redirecionado para a tela de login em
/// vez de ver o shell/estado vazio do dashboard. As telas de conta usam o
/// <c>LayoutAutenticacao</c> e, por isso, permanecem acessíveis (sem laço de redirecionamento).
/// A decisão de autorização continua sendo do servidor; este teste cobre apenas a UX de entrada.
/// Sobe o host no ambiente "Testing" (sem exigir banco).
/// </summary>
[Collection(ColecaoHostWeb.Nome)]
public sealed class RedirecionamentoAutenticacaoTests : IClassFixture<RedirecionamentoAutenticacaoTests.Fabrica>
{
    private readonly Fabrica _fabrica;

    public RedirecionamentoAutenticacaoTests(Fabrica fabrica) => _fabrica = fabrica;

    [Fact] // Rota protegida sem autenticação → redireciona para o login preservando o destino.
    public async Task Rota_Protegida_Anonima_Redireciona_Para_Login()
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resposta = await cliente.GetAsync(new Uri("/", UriKind.Relative));

        ((int)resposta.StatusCode).Should().BeInRange(300, 399);
        resposta.Headers.Location.Should().NotBeNull();
        resposta.Headers.Location!.OriginalString.Should().Contain("/conta/entrar");
    }

    [Fact] // A própria tela de login é pública (LayoutAutenticacao) — não pode entrar em laço.
    public async Task Tela_De_Login_Permanece_Acessivel_Sem_Autenticacao()
    {
        using var cliente = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resposta = await cliente.GetAsync(new Uri("/conta/entrar", UriKind.Relative));
        var corpo = await resposta.Content.ReadAsStringAsync();

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        corpo.Should().Contain("/conta/login"); // o formulário de credenciais é servido
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
