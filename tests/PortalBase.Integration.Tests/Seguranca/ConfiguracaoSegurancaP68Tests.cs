using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Identidade;
using PortalBase.Infrastructure.DependencyInjection;
using PortalBase.Web.Seguranca;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Configuração de segurança CONFIGURÁVEL e fail-fast (Prompt 68): limites de rate limiting
/// (<see cref="OpcoesRateLimit"/>) e política de lockout (<see cref="PoliticaAutenticacao"/> ligada a
/// 'Seguranca:Lockout'). Provamos: padrões seguros idênticos ao anterior quando ausentes; sobreposição real
/// quando presentes; e FALHA no startup (nunca relaxar em silêncio) para valores inválidos.
/// </summary>
public sealed class ConfiguracaoSegurancaP68Tests
{
    private static IConfiguration Config(params (string Chave, string? Valor)[] pares)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pares.ToDictionary(p => p.Chave, p => p.Valor))
            .Build();

    // ---- Rate limiting (Web) ----

    [Fact]
    public void RateLimit_Padroes_Reproduzem_Comportamento_Anterior()
    {
        var opcoes = new OpcoesRateLimit();
        opcoes.LoginPorJanela.Should().Be(10);
        opcoes.MfaPorJanela.Should().Be(10);
        opcoes.UploadPorJanela.Should().Be(30);
        opcoes.Janela.Should().Be(TimeSpan.FromMinutes(5));
        opcoes.Invoking(o => o.Validar()).Should().NotThrow();
    }

    [Fact]
    public void RateLimit_Vincula_Da_Configuracao()
    {
        var opcoes = new OpcoesRateLimit();
        Config(
            ("Seguranca:RateLimit:LoginPorJanela", "3"),
            ("Seguranca:RateLimit:JanelaMinutos", "2"))
            .GetSection(OpcoesRateLimit.Secao).Bind(opcoes);

        opcoes.LoginPorJanela.Should().Be(3);
        opcoes.Janela.Should().Be(TimeSpan.FromMinutes(2));
        opcoes.UploadPorJanela.Should().Be(30); // não sobreposto ⇒ padrão
    }

    [Theory]
    [InlineData(0, 10, 30, 5)]
    [InlineData(10, 10, 30, 0)]
    [InlineData(10, -1, 30, 5)]
    public void RateLimit_Valores_Invalidos_Falham_Fast(int login, int mfa, int upload, int janela)
    {
        var opcoes = new OpcoesRateLimit
        {
            LoginPorJanela = login,
            MfaPorJanela = mfa,
            UploadPorJanela = upload,
            JanelaMinutos = janela,
        };
        opcoes.Invoking(o => o.Validar()).Should().Throw<InvalidOperationException>();
    }

    // ---- Lockout (Infrastructure liga 'Seguranca:Lockout' → PoliticaAutenticacao) ----

    private static PoliticaAutenticacao PoliticaRegistrada(IConfiguration configuracao)
    {
        var servicos = new ServiceCollection();
        servicos.AddInfrastructure(configuracao, conexaoObrigatoria: false, producao: false);
        return servicos
            .Where(d => d.ServiceType == typeof(PoliticaAutenticacao))
            .Select(d => d.ImplementationInstance)
            .OfType<PoliticaAutenticacao>()
            .Last();
    }

    [Fact]
    public void Lockout_Sem_Configuracao_Usa_Padrao_Conservador()
    {
        var politica = PoliticaRegistrada(Config(("Vault:Modo", "ConfiguracaoLocal")));
        politica.MaxTentativas.Should().Be(5);
        politica.DuracaoBloqueio.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Lockout_Vincula_Da_Configuracao()
    {
        var politica = PoliticaRegistrada(Config(
            ("Vault:Modo", "ConfiguracaoLocal"),
            ("Seguranca:Lockout:MaxTentativas", "9"),
            ("Seguranca:Lockout:DuracaoBloqueioMinutos", "42")));

        politica.MaxTentativas.Should().Be(9);
        politica.DuracaoBloqueio.Should().Be(TimeSpan.FromMinutes(42));
    }

    [Fact]
    public void Lockout_Valor_Invalido_Falha_Fast_No_Startup()
    {
        var acao = () => PoliticaRegistrada(Config(
            ("Vault:Modo", "ConfiguracaoLocal"),
            ("Seguranca:Lockout:MaxTentativas", "0")));

        acao.Should().Throw<InvalidOperationException>();
    }
}
