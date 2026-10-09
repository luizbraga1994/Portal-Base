using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Tarefas;
using PortalBase.Domain.Tarefas;
using PortalBase.Web.Seguranca;
using PortalBase.Web.Tarefas;
using PortalBase.Web.Ui.Dados;

namespace PortalBase.Integration.Tests.Tarefas;

/// <summary>
/// ARQ-006 (auditoria de 08/10/2026) — a parte da WEB: a identidade da tarefa (quem pediu, sem MFA, nunca dentro de uma
/// requisição), quando o executor liga, o recuo depois de falhas e a tradução da situação da tarefa para a tela.
/// </summary>
public sealed class TarefasWebTests
{
    private static ServiceProvider Provedor()
    {
        var servicos = new ServiceCollection();
        servicos.AddHttpContextAccessor();
        servicos.AddScoped<PortadorPrincipalCircuito>();
        servicos.AddScoped<IContextoSeguranca, ContextoSegurancaHttp>();
        return servicos.BuildServiceProvider();
    }

    [Fact]
    public void Identidade_Da_Tarefa_E_A_De_Quem_Pediu_Sem_Mfa()
    {
        using var provedor = Provedor();
        using var escopo = provedor.CreateScope();
        var solicitante = Guid.NewGuid();

        new DefinidorIdentidadeTarefaWeb().Definir(escopo.ServiceProvider, solicitante);

        var seguranca = escopo.ServiceProvider.GetRequiredService<IContextoSeguranca>();
        seguranca.EstaAutenticado.Should().BeTrue();
        seguranca.PrincipalId.Should().Be(solicitante.ToString());
        seguranca.MfaSatisfeito.Should().BeFalse("operação que exigir step-up falha na tarefa (fail-closed)");
    }

    [Fact]
    public void Sem_Solicitante_Ou_Dentro_De_Uma_Requisicao_Recusa()
    {
        using var provedor = Provedor();
        using var escopo = provedor.CreateScope();
        var definidor = new DefinidorIdentidadeTarefaWeb();

        ((Action)(() => definidor.Definir(escopo.ServiceProvider, Guid.Empty))).Should().Throw<NaoAutenticadoException>();

        escopo.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
        try
        {
            ((Action)(() => definidor.Definir(escopo.ServiceProvider, Guid.NewGuid())))
                .Should().Throw<InvalidOperationException>("o usuário da requisição teria precedência sobre o de quem pediu");
        }
        finally
        {
            escopo.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        }
    }

    [Theory]
    [InlineData("Production", null, true)]
    [InlineData("Development", null, true)]
    [InlineData("Testing", null, false)]
    [InlineData("Testing", "true", true)]
    [InlineData("Production", "false", false)]
    public void Executor_Liga_Por_Padrao_Exceto_Nos_Testes_Automatizados(string ambiente, string? configurado, bool esperado)
    {
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(configurado is null ? [] : new Dictionary<string, string?> { ["Tarefas:ExecutorHabilitado"] = configurado })
            .Build();

        ServicosTarefasWeb.ExecutorHabilitado(configuracao, new Ambiente(ambiente)).Should().Be(esperado);
    }

    [Fact]
    public void Depois_De_Falhas_Seguidas_Espera_Cada_Vez_Mais_Ate_5_Minutos()
    {
        HospedeiroTarefas.EsperaAposFalha(1).Should().Be(TimeSpan.FromSeconds(5));
        HospedeiroTarefas.EsperaAposFalha(2).Should().Be(TimeSpan.FromSeconds(10));
        HospedeiroTarefas.EsperaAposFalha(4).Should().Be(TimeSpan.FromSeconds(40));
        HospedeiroTarefas.EsperaAposFalha(50).Should().Be(TimeSpan.FromMinutes(5));
    }

    private sealed class Ambiente(string nome) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nome;

        public string ApplicationName { get; set; } = "PortalBase.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
