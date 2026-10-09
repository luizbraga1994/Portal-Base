using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PortalBase.Infrastructure.Observabilidade;

namespace PortalBase.Integration.Tests.Observabilidade;

/// <summary>
/// OBS-004 — a medição de disco da rotina de alertas, no sistema de arquivos REAL: pastas no mesmo disco viram um
/// registro com todos os usos; pasta de rede e pasta não configurada ficam de fora; a unidade aparece, o caminho não.
/// </summary>
public sealed class VerificadorEspacoDiscoTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "portalbase-disco-" + Guid.NewGuid().ToString("N"));

    public VerificadorEspacoDiscoTests() => Directory.CreateDirectory(_raiz);

    public void Dispose() => Directory.Delete(_raiz, recursive: true);

    [Fact]
    public void Pastas_No_Mesmo_Disco_Viram_Um_Registro_Com_Todos_Os_Usos()
    {
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ArmazenamentoBackup:DiretorioRaiz"] = Path.Combine(_raiz, "backups"),
            ["Observabilidade:LogArquivo:Diretorio"] = Path.Combine(_raiz, "logs"),
            ["ArmazenamentoBackup:DiretorioOffsite"] = @"\\servidor-backup\PortalBase",
        }).Build();

        var discos = VerificadorEspacoDisco.DaConfiguracao(configuracao).Medir();

        discos.Should().ContainSingle("as duas pastas estão no mesmo disco; a de rede não é medida");
        var disco = discos[0];
        disco.Usos.Should().Be("backups, logs");
        disco.TotalBytes.Should().BePositive();
        disco.LivreBytes.Should().BeInRange(0, disco.TotalBytes);
        disco.PercentualLivre.Should().BeInRange(0, 100);
        disco.Unidade.Should().NotContain("portalbase-disco", "só a unidade, nunca o caminho da pasta");
    }

    [Fact]
    public void Sem_Pastas_Configuradas_Nao_Mede_Nada()
        => VerificadorEspacoDisco.DaConfiguracao(new ConfigurationBuilder().Build()).Medir().Should().BeEmpty();

    [Fact]
    public void Disco_Que_Nao_Responde_Fica_De_Fora_Sem_Lancar()
    {
        var verificador = new VerificadorEspacoDisco([("backups", _raiz)], () => []);

        verificador.Medir().Should().BeEmpty();
    }
}
