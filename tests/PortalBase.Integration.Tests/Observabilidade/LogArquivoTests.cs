using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PortalBase.Infrastructure.Observabilidade;
using Serilog;

namespace PortalBase.Integration.Tests.Observabilidade;

/// <summary>
/// OBS-001 (auditoria de 08/10/2026) — log em ARQUIVO. Antes, em produção (IIS e serviço do Windows, sem console) só os
/// avisos e erros iam para o Log de Aplicativo: o Information — o que aconteceu antes de um erro — se perdia. Provas com
/// o sink real do Serilog em disco: o Information chega ao arquivo do dia do host, os arquivos mais velhos que a
/// retenção são apagados e, sem pasta configurada, nada é gravado.
/// </summary>
public sealed class LogArquivoTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "portalbase-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }

    private IConfiguration Configuracao(int? dias = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observabilidade:LogArquivo:Diretorio"] = _pasta,
            ["Observabilidade:LogArquivo:DiasRetencao"] = dias?.ToString(CultureInfo.InvariantCulture),
        })
        .Build();

    private static string Hoje => DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    [Fact]
    public void Information_Vai_Para_O_Arquivo_Do_Dia_Do_Host()
    {
        using (var log = new LoggerConfiguration()
                   .MinimumLevel.Information()
                   .Enrich.WithProperty("Application", "PortalBase.Web")
                   .GravarEmArquivoSeConfigurado(Configuracao(), "PortalBase.Web")
                   .CreateLogger())
        {
            log.Information("Importação {Numero} concluída", 42);
        }

        var arquivo = Path.Combine(_pasta, $"PortalBase.Web-{Hoje}.log");
        File.Exists(arquivo).Should().BeTrue();
        var conteudo = File.ReadAllText(arquivo);
        conteudo.Should().Contain("[INF] PortalBase.Web").And.Contain("Importação 42 concluída");
    }

    [Fact]
    public void Arquivos_Mais_Velhos_Que_A_Retencao_Sao_Apagados()
    {
        Directory.CreateDirectory(_pasta);
        string Antigo(int dias) => Path.Combine(_pasta, $"PortalBase.Worker-{DateTime.Now.AddDays(-dias).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
        foreach (var dias in new[] { 40, 10, 3, 1 })
        {
            File.WriteAllText(Antigo(dias), "linha antiga\n");
        }

        File.WriteAllText(Path.Combine(_pasta, "outro-programa.log"), "não é do portal\n");

        using (var log = new LoggerConfiguration().GravarEmArquivoSeConfigurado(Configuracao(dias: 2), "PortalBase.Worker").CreateLogger())
        {
            log.Information("abrindo o arquivo de hoje");
        }

        Directory.GetFiles(_pasta).Select(Path.GetFileName).Should().BeEquivalentTo(
            [$"PortalBase.Worker-{Hoje}.log", Path.GetFileName(Antigo(1)), "outro-programa.log"],
            "só ficam os arquivos dentro dos 2 dias de retenção; arquivo de outro programa não é tocado");
    }

    [Fact]
    public void Sem_Pasta_Configurada_Nada_E_Gravado()
    {
        var vazia = new ConfigurationBuilder().Build();

        LogArquivo.Ler(vazia).Diretorio.Should().BeNull();
        using (var log = new LoggerConfiguration().GravarEmArquivoSeConfigurado(vazia, "PortalBase.Web").CreateLogger())
        {
            log.Information("só no console");
        }

        Directory.Exists(_pasta).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(30, 30)]
    [InlineData(99_999, 3650)]
    public void Retencao_Fica_Dentro_Dos_Limites(int configurado, int efetivo)
        => LogArquivo.Ler(Configuracao(configurado)).DiasRetencao.Should().Be(efetivo);
}
