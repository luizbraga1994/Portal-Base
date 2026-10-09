using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;
using PortalBase.Infrastructure.Producao;

namespace PortalBase.Integration.Tests.Producao;

/// <summary>
/// Configuração do servidor próprio (<c>%ProgramData%\PortalBase\&lt;host&gt;.json</c>, gravada pelo configurar-iis.ps1):
/// lida só em produção, sem segredo (regra 7), depois dos appsettings e antes das variáveis de ambiente (que prevalecem), e com mensagem clara
/// quando falta, está ilegível ou inválida. Cada teste usa uma pasta temporária própria (nada de variável de ambiente
/// do processo).
/// </summary>
public sealed class ConfiguracaoServidorTests : IDisposable
{
    private const string Host = "PortalBase.Web";
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "portalbase-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfiguracaoServidorTests() => Directory.CreateDirectory(_pasta);

    public void Dispose() => Directory.Delete(_pasta, recursive: true);

    private string Gravar(string json)
    {
        var caminho = Path.Combine(_pasta, Host + ".json");
        File.WriteAllText(caminho, json);
        return caminho;
    }

    private const string JsonValido = """
        {
          "ConnectionStrings": { "Catalogo": "Server=db;Port=3306;Database=portalbase_catalogo;User Id=portalbase_catalogo;SslMode=Required;" },
          "BancoCatalogo": { "SenhaRef": "portalbase-prod-mysql-catalogo" },
          "Vault": { "Modo": "CofreWindows", "DiretorioCofreWindows": "D:\\PortalBase\\cofre" },
          "Armazenamento": { "DiretorioRaiz": "D:\\PortalBase\\dados" },
          "BancoTenant": { "RuntimeSenhaRef": "portalbase-prod-mysql-runtime" },
          "SegurancaIdentidade": { "ChaveProtecaoMfaRef": "portalbase-prod-chave-mfa" }
        }
        """;

    [Fact]
    public void Em_Producao_O_Arquivo_Completa_A_Configuracao_E_A_Pre_Checagem_Passa()
    {
        Gravar(JsonValido);
        var configuracao = new ConfigurationManager();

        var usado = ConfiguracaoServidor.Adicionar(configuracao, "Production", Host, _pasta);

        usado.Should().Be(Path.Combine(_pasta, Host + ".json"));
        configuracao.GetConnectionString("Catalogo").Should().Contain("Database=portalbase_catalogo").And.NotContain("Password");
        configuracao["Vault:Modo"].Should().Be("CofreWindows");
        var preflight = () => PreflightProducao.Exigir(configuracao, "Production");
        preflight.Should().NotThrow("o arquivo traz catálogo, cofre e armazenamento — o que faltava ao rodar o .exe à mão");
    }

    [Fact]
    public void Variaveis_De_Ambiente_Prevalecem_Sobre_O_Arquivo()
    {
        Gravar(JsonValido);
        var prefixo = "PORTALBASE_TESTE_" + Guid.NewGuid().ToString("N")[..8] + "_";
        Environment.SetEnvironmentVariable(prefixo + "Vault__Modo", "KeyVault");
        try
        {
            var configuracao = new ConfigurationManager();
            configuracao.AddInMemoryCollection([new("Vault:Modo", "appsettings"), new("Armazenamento:DiretorioRaiz", "appsettings")]);
            configuracao.AddEnvironmentVariables(prefixo);

            ConfiguracaoServidor.Adicionar(configuracao, "Production", Host, _pasta);

            configuracao["Vault:Modo"].Should().Be("KeyVault", "variável de ambiente (ex.: do pool do IIS) vence o arquivo");
            configuracao["Armazenamento:DiretorioRaiz"].Should().Be(@"D:\PortalBase\dados", "o arquivo vence o appsettings");
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefixo + "Vault__Modo", null);
        }
    }

    // Pasta publicada como no servidor: appsettings com o "Catalogo": "" de exemplo (o defeito do primeiro deploy: o
    // arquivo do servidor entrava antes dos appsettings e esse vazio apagava a string de conexão).
    private string PastaPublicada()
    {
        var pasta = Path.Combine(_pasta, "publicado");
        Directory.CreateDirectory(pasta);
        const string vazio = """{ "ConnectionStrings": { "Catalogo": "" }, "Serilog": { "MinimumLevel": { "Default": "Information" } } }""";
        File.WriteAllText(Path.Combine(pasta, "appsettings.json"), vazio);
        File.WriteAllText(Path.Combine(pasta, "appsettings.Production.json"), vazio);
        return pasta;
    }

    [Fact]
    public void Worker_Real_Em_Producao_Usa_A_String_De_Conexao_Do_Arquivo_E_Nao_O_Vazio_Do_Appsettings()
    {
        Gravar(JsonValido);
        var configuracaoHost = new ConfigurationManager();
        configuracaoHost.AddInMemoryCollection([new("hostBuilder:reloadConfigOnChange", "false")]);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = PastaPublicada(),
            Args = [],
            Configuration = configuracaoHost,
        });

        ConfiguracaoServidor.Adicionar(builder.Configuration, builder.Environment.EnvironmentName, Host, _pasta).Should().NotBeNull();

        builder.Configuration.GetConnectionString("Catalogo").Should().Contain("Database=portalbase_catalogo");
        var preflight = () => PreflightProducao.Exigir(builder.Configuration, builder.Environment.EnvironmentName);
        preflight.Should().NotThrow();
    }

    [Fact]
    public void Web_Real_Em_Producao_Usa_A_String_De_Conexao_Do_Arquivo_E_Nao_O_Vazio_Do_Appsettings()
    {
        Gravar(JsonValido);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = PastaPublicada(),
            Args = ["--hostBuilder:reloadConfigOnChange=false"],
        });

        ConfiguracaoServidor.Adicionar(builder.Configuration, builder.Environment.EnvironmentName, Host, _pasta).Should().NotBeNull();

        builder.Configuration.GetConnectionString("Catalogo").Should().Contain("Database=portalbase_catalogo");
        builder.Configuration["Vault:Modo"].Should().Be("CofreWindows");
        var preflight = () => PreflightProducao.Exigir(builder.Configuration, builder.Environment.EnvironmentName);
        preflight.Should().NotThrow();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void Fora_De_Producao_O_Arquivo_E_Ignorado(string? ambiente)
    {
        Gravar(JsonValido);
        var configuracao = new ConfigurationManager();

        ConfiguracaoServidor.Adicionar(configuracao, ambiente, Host, _pasta).Should().BeNull();
        configuracao["Vault:Modo"].Should().BeNull("na máquina de desenvolvimento o arquivo de produção não pode trocar a configuração");
    }

    [Fact]
    public void Sem_Arquivo_Nada_Muda()
    {
        var configuracao = new ConfigurationManager();

        ConfiguracaoServidor.Adicionar(configuracao, "Production", Host, _pasta).Should().BeNull();
        configuracao.Sources.OfType<JsonConfigurationSource>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "ConnectionStrings": { "Catalogo": "Server=db;User Id=app;Password=Segredo#1;" } }""", "ConnectionStrings:Catalogo")]
    [InlineData("""{ "ConnectionStrings": { "Catalogo": "Server=db;User Id=app; pwd = Segredo#1" } }""", "ConnectionStrings:Catalogo")]
    [InlineData("""{ "Smtp": { "Senha": "Segredo#1" } }""", "Smtp:Senha")]
    [InlineData("""{ "DeRe": { "Integration": { "ClientSecret": "Segredo#1" } } }""", "DeRe:Integration:ClientSecret")]
    public void Arquivo_Com_Segredo_Aborta_Sem_Mostrar_O_Valor(string json, string chave)
    {
        Gravar(json);
        var configuracao = new ConfigurationManager();

        var adicionar = () => ConfiguracaoServidor.Adicionar(configuracao, "Production", Host, _pasta);

        adicionar.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains(chave, StringComparison.Ordinal) && !e.Message.Contains("Segredo#1", StringComparison.Ordinal));
        configuracao.Sources.OfType<JsonConfigurationSource>().Should().BeEmpty("nada do arquivo recusado entra na configuração");
    }

    [Theory]
    [InlineData("BancoCatalogo:SenhaRef", "portalbase-prod-mysql-catalogo", false)]
    [InlineData("SegurancaIdentidade:ChaveHashRef", "portalbase-prod-chave-hash", false)]
    [InlineData("DeRe:Integration:ReferenciaClientSecret", "portalbase-prod-dere", false)]
    [InlineData("Smtp:Usuario", "portal@empresa.com.br", false)]
    [InlineData("ConnectionStrings:Catalogo", "Server=db;User Id=x;SslMode=Required;", false)]
    [InlineData("Smtp:Senha", "x", true)]
    [InlineData("Qualquer:Token", "x", true)]
    [InlineData("ConnectionStrings:Catalogo", "Server=db;Password=x", true)]
    public void Detecta_Segredo_Pelo_Nome_Ou_Pela_Senha_Na_Conexao(string chave, string valor, bool segredo)
        => ConfiguracaoServidor.EhSegredo(chave, valor).Should().Be(segredo);

    [Fact]
    public void Json_Invalido_Da_Mensagem_Clara()
    {
        Gravar("{ \"Vault\": ");
        var configuracao = new ConfigurationManager();

        var adicionar = () => ConfiguracaoServidor.Adicionar(configuracao, "Production", Host, _pasta);

        adicionar.Should().Throw<InvalidOperationException>().WithMessage("*não é um JSON válido*configurar-iis.ps1*");
    }

    [Fact]
    public void Pre_Checagem_Diz_Onde_A_Configuracao_E_Esperada()
    {
        var configuracao = new ConfigurationBuilder().Build();

        var exigir = () => PreflightProducao.Exigir(configuracao, "Production", ConfiguracaoServidor.Dica(Host));

        exigir.Should().Throw<InvalidOperationException>()
            .WithMessage("*Faltando: ConnectionStrings:Catalogo*")
            .WithMessage("*" + ConfiguracaoServidor.Caminho(Host) + "*configurar-iis.ps1*");
    }
}
