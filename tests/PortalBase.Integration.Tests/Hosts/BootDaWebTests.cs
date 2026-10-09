using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Integration.Tests.Hosts;

/// <summary>
/// Valida o boot do host Web: criação do host, composição de DI, configuração e
/// health check de liveness — sem depender de banco (a liveness não toca o banco).
/// </summary>
[Collection(ColecaoHostWeb.Nome)]
public sealed class BootDaWebTests : IClassFixture<BootDaWebTests.FabricaWeb>
{
    private readonly FabricaWeb _fabrica;

    public BootDaWebTests(FabricaWeb fabrica) => _fabrica = fabrica;

    [Fact]
    public async Task Host_Inicia_E_Liveness_RetornaSaudavel()
    {
        using var cliente = _fabrica.CreateClient();

        using var resposta = await cliente.GetAsync(new Uri("/health/live", UriKind.Relative));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var corpo = await resposta.Content.ReadAsStringAsync();
        corpo.Should().Contain("Healthy");
    }

    /// <summary>
    /// OBS-004: a rotina de alertas está composta na Web (todas as dependências resolvem, inclusive a fila de e-mail)
    /// e, nos testes automatizados (Testing), não roda em segundo plano no meio das telas.
    /// </summary>
    [Fact]
    public void Rotina_De_Alertas_Esta_Composta_E_Nao_Roda_Em_Segundo_Plano_Nos_Testes()
    {
        using var escopo = _fabrica.Services.CreateScope();

        escopo.ServiceProvider.GetRequiredService<CicloAlertasOperacionais>().Should().NotBeNull();
        _fabrica.Services.GetRequiredService<EstadoAlertasNotificados>()
            .Should().BeSameAs(_fabrica.Services.GetRequiredService<EstadoAlertasNotificados>(), "o que já foi notificado vale para o processo inteiro");
        var hospedados = _fabrica.Services.GetServices<IHostedService>().ToList();
        hospedados.Should().Contain(s => s is PortalBase.Web.Email.DisparadorEmail);
        hospedados.Should().NotContain(s => s is PortalBase.Web.Observabilidade.MonitorAlertas);
    }

    /// <summary>
    /// ARQ-006: o trabalho pesado passa pela fila de tarefas; o executor e o manipulador de exemplo estão compostos na
    /// Web, mas o laço em segundo plano não sobe nos testes automatizados sem ser pedido.
    /// </summary>
    [Fact]
    public void Tarefas_Em_Segundo_Plano_Estao_Compostas_E_O_Executor_So_Roda_Quando_Pedido_Nos_Testes()
    {
        using var escopo = _fabrica.Services.CreateScope();

        escopo.ServiceProvider.GetRequiredService<PortalBase.Application.Tarefas.FilaTarefasSegundoPlano>().Should().NotBeNull();
        escopo.ServiceProvider.GetServices<PortalBase.Application.Tarefas.IManipuladorTarefa>().Select(m => m.Tipo)
            .Should().BeEquivalentTo([PortalBase.Domain.Tarefas.TipoTarefaSegundoPlano.Exemplo]);
        _fabrica.Services.GetRequiredService<PortalBase.Application.Tarefas.ExecutorTarefasSegundoPlano>().Should().NotBeNull();
        _fabrica.Services.GetServices<IHostedService>().Should().NotContain(s => s is PortalBase.Web.Tarefas.HospedeiroTarefas);
    }

    /// <summary>Fábrica que sobe o host Web no ambiente "Testing" (não-Produção, sem exigir banco).</summary>
    public sealed class FabricaWeb : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Testing");
        }
    }
}
