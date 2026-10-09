using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Infrastructure.Segredos;
using PortalBase.Infrastructure.Tempo;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>Seleção do provedor de segredos por ambiente e fail-fast (Prompt 06, seções 5-7).</summary>
public sealed class VaultSelecaoTests
{
    private static IConfiguration Config(params (string Chave, string? Valor)[] pares)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pares.ToDictionary(p => p.Chave, p => p.Valor))
            .Build();

    [Fact] // §7 — Produção sem cofre gerenciado NÃO inicia (fail-fast, sem fallback silencioso)
    public void Producao_Sem_KeyVault_Falha_Fast()
    {
        var acao = () => new ServiceCollection().AddSegredos(Config(("Vault:Modo", "ConfiguracaoLocal")), producao: true);
        acao.Should().Throw<InvalidOperationException>();
    }

    [Fact] // §5 — Produção com KeyVault mas sem endpoint também falha
    public void Producao_KeyVault_Sem_Endpoint_Falha()
    {
        var acao = () => new ServiceCollection().AddSegredos(Config(("Vault:Modo", "KeyVault")), producao: true);
        acao.Should().Throw<InvalidOperationException>();
    }

    [Fact] // §6 — Dev/Test usa o provedor por configuração (nunca em Produção)
    public void Dev_Usa_Provedor_Configuracao()
    {
        var configuracao = Config(("Vault:Modo", "ConfiguracaoLocal"), ("Segredos:x", "valor"));
        var servicos = new ServiceCollection();
        servicos.AddSingleton(configuracao);
        servicos.AddSegredos(configuracao, producao: false);

        using var provedor = servicos.BuildServiceProvider();
        provedor.GetRequiredService<IProvedorSegredos>().Should().BeOfType<ProvedorSegredosConfiguracao>();
    }

    [Fact] // §5/§11 — modo KeyVault registra o provedor de cofre com cache (autenticação preguiçosa)
    public void KeyVault_Registra_Provedor_Com_Cache()
    {
        var configuracao = Config(("Vault:Modo", "KeyVault"), ("Vault:Endpoint", "https://cofre-teste.vault.azure.net/"));
        var servicos = new ServiceCollection();
        servicos.AddSingleton<IRelogio, RelogioSistema>();
        servicos.AddSegredos(configuracao, producao: false);

        using var provedor = servicos.BuildServiceProvider();
        provedor.GetRequiredService<IProvedorSegredos>().Should().BeOfType<ProvedorSegredosCache>();
    }
}
