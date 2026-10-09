using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PortalBase.Infrastructure.Producao;

namespace PortalBase.Integration.Tests.Producao;

/// <summary>
/// Testes do FAIL-FAST de startup em produção (Prompt 33, §9/§10). Não requer MySQL/Docker — usa
/// configuração em memória. Verifica que produção sem config essencial aborta o startup listando as
/// chaves faltantes (sem expor valores), que produção completa sobe, e que Dev é no-op.
/// </summary>
public sealed class PreflightProducaoTests
{
    private static IConfiguration Config(Dictionary<string, string?> valores)
        => new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

    [Fact] // Produção sem config essencial ⇒ InvalidOperationException listando as chaves.
    public void Producao_SemConfig_Aborta()
    {
        var config = Config(new Dictionary<string, string?>());

        var acao = () => PreflightProducao.Exigir(config, "Production");

        acao.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:Catalogo*")
            .WithMessage("*Vault:Modo*")
            .WithMessage("*Armazenamento:DiretorioRaiz*");
    }

    [Fact] // Produção completa ⇒ não lança.
    public void Producao_Completa_NaoAborta()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Catalogo"] = "server=db;database=cat",
            ["Vault:Modo"] = "KeyVault",
            ["Armazenamento:DiretorioRaiz"] = "/var/portalbase/dados",
        });

        var acao = () => PreflightProducao.Exigir(config, "Production");

        acao.Should().NotThrow();
        PreflightProducao.Avaliar(config, "Production").Ok.Should().BeTrue();
    }

    [Fact] // Produção Restrita com a configuração essencial ⇒ Ok.
    public void ProducaoRestrita_Com_Config_Essencial_Ok()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Catalogo"] = "server=db;database=cat",
            ["Vault:Modo"] = "KeyVault",
            ["Armazenamento:DiretorioRaiz"] = "/var/portalbase/dados",
        });

        PreflightProducao.Avaliar(config, "ProductionRestricted").Ok.Should().BeTrue();
    }

    [Fact] // Dev ⇒ no-op mesmo sem nenhuma config.
    public void Desenvolvimento_SemConfig_NaoAborta()
    {
        var config = Config([]);
        var acao = () => PreflightProducao.Exigir(config, "Development");
        acao.Should().NotThrow();
    }
}
