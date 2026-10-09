using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Infrastructure.Segredos;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Development: o que a tela grava no "cofre" local (segredos de integração do tenant) vai também para o arquivo de
/// user-secrets e sobrevive ao reinício; sem arquivo (Test), fica só na memória. JSON inválido nunca é sobrescrito.
/// </summary>
public sealed class ProvedorSegredosArquivoLocalTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "portalbase-segredos-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }

    [Fact]
    public async Task Grava_No_Arquivo_Preservando_As_Outras_Chaves_E_Vale_Depois_Do_Reinicio()
    {
        var arquivo = Path.Combine(_pasta, "secrets.json");
        Directory.CreateDirectory(_pasta);
        await File.WriteAllTextAsync(arquivo, "{\n  \"ConnectionStrings:Catalogo\": \"server=localhost\"\n}");

        var provedor = new ProvedorSegredosConfiguracao(new ConfigurationBuilder().Build(), arquivo);
        await provedor.DefinirAsync("portalbase-dev-tenant-x-a1-password", "senha-pfx");

        (await provedor.ObterAsync("portalbase-dev-tenant-x-a1-password")).Should().Be("senha-pfx", "vale já neste processo");

        // "Reinício": um provedor novo lendo a configuração do arquivo.
        var config = new ConfigurationBuilder().AddJsonFile(arquivo).Build();
        config["ConnectionStrings:Catalogo"].Should().Be("server=localhost", "as outras chaves continuam");
        (await new ProvedorSegredosConfiguracao(config).ObterAsync("portalbase-dev-tenant-x-a1-password")).Should().Be("senha-pfx");
        File.Exists(arquivo + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Cria_A_Pasta_E_O_Arquivo_Quando_Nao_Existem()
    {
        var arquivo = Path.Combine(_pasta, "sub", "secrets.json");

        await new ProvedorSegredosConfiguracao(new ConfigurationBuilder().Build(), arquivo).DefinirAsync("ref", "valor");

        new ConfigurationBuilder().AddJsonFile(arquivo).Build()["Segredos:ref"].Should().Be("valor");
    }

    [Fact]
    public async Task Sem_Arquivo_Fica_So_Na_Memoria()
    {
        var provedor = new ProvedorSegredosConfiguracao(new ConfigurationBuilder().Build());

        await provedor.DefinirAsync("ref", "valor");

        (await provedor.ObterAsync("ref")).Should().Be("valor");
        Directory.Exists(_pasta).Should().BeFalse();
    }

    [Fact]
    public async Task Json_Invalido_Nao_E_Sobrescrito_E_Nada_Muda()
    {
        var arquivo = Path.Combine(_pasta, "secrets.json");
        Directory.CreateDirectory(_pasta);
        await File.WriteAllTextAsync(arquivo, "{ isto não é json");
        var provedor = new ProvedorSegredosConfiguracao(new ConfigurationBuilder().Build(), arquivo);

        var acao = async () => await provedor.DefinirAsync("ref", "valor");

        await acao.Should().ThrowAsync<InvalidOperationException>();
        (await File.ReadAllTextAsync(arquivo)).Should().Be("{ isto não é json");
        var ler = async () => await provedor.ObterAsync("ref");
        await ler.Should().ThrowAsync<InvalidOperationException>("o valor não foi aceito");
    }

    [Fact]
    public async Task AddSegredos_Usa_O_Arquivo_Configurado_Fora_De_Producao()
    {
        var arquivo = Path.Combine(_pasta, "secrets.json");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:Modo"] = "ConfiguracaoLocal",
            ["Vault:ArquivoSegredosLocal"] = arquivo,
        }).Build();
        var servicos = new ServiceCollection().AddSingleton<IConfiguration>(config);
        servicos.AddSegredos(config, producao: false);
        await using var sp = servicos.BuildServiceProvider();

        await sp.GetRequiredService<IEscritorSegredos>().DefinirAsync("ref", "valor");

        new ConfigurationBuilder().AddJsonFile(arquivo).Build()["Segredos:ref"].Should().Be("valor");
    }
}
