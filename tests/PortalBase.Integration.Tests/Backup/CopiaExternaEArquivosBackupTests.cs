using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Backup;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Integration.Tests.Backup;

/// <summary>
/// Onda 2 da auditoria de 08/10/2026 — implementações REAIS em disco temporário:
/// <list type="bullet">
/// <item>BKP-003: a cópia externa só ganha o nome definitivo depois de conferida pelo SHA-256 relido do destino;
/// divergência não deixa arquivo com cara de backup bom; referência hostil não escapa da raiz.</item>
/// <item>BKP-005: o pacote de arquivos de um contribuinte leva só a pasta DELE, nunca a de outro contribuinte, outra
/// pasta da raiz nem o que um link simbólico aponte para fora.</item>
/// </list>
/// </summary>
public sealed class CopiaExternaEArquivosBackupTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "portalbase-onda2-" + Guid.NewGuid().ToString("N"));

    public CopiaExternaEArquivosBackupTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    private static CopiaExternaBackupFilesystem Copia(string? diretorio)
        => new(Options.Create(new OpcoesArmazenamentoBackup { DiretorioOffsite = diretorio ?? string.Empty }));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---- Cópia externa ----

    [Fact]
    public async Task Copia_Conferida_Fica_Com_O_Mesmo_Conteudo_E_Sem_Temporario()
    {
        var raiz = Path.Combine(_base, "offsite");
        var copia = Copia(raiz);
        var artefato = Encoding.UTF8.GetBytes("ARTEFATO-CIFRADO");
        const string referencia = "tenantcompleto/aaaa/20261008T030000Z-x.bak.enc";

        copia.Configurada.Should().BeTrue();
        await copia.CopiarAsync(referencia, new MemoryStream(artefato), Hash(artefato));

        var destino = Path.Combine(raiz, "tenantcompleto", "aaaa", "20261008T030000Z-x.bak.enc");
        (await File.ReadAllBytesAsync(destino)).Should().Equal(artefato);
        Directory.GetFiles(raiz, "*.parcial", SearchOption.AllDirectories).Should().BeEmpty();

        await copia.RemoverAsync(referencia);
        File.Exists(destino).Should().BeFalse();
        await copia.RemoverAsync(referencia); // repetir não falha (retenção idempotente)
    }

    [Fact]
    public async Task Copia_Que_Nao_Confere_Nao_Deixa_Arquivo_No_Destino()
    {
        var raiz = Path.Combine(_base, "offsite");
        var copia = Copia(raiz);
        var artefato = Encoding.UTF8.GetBytes("ARTEFATO-CIFRADO");

        var acao = async () => await copia.CopiarAsync("catalogocompleto/catalogo/x.bak.enc", new MemoryStream(artefato), Hash("OUTRO"u8.ToArray()));

        await acao.Should().ThrowAsync<InvalidDataException>();
        Directory.GetFiles(raiz, "*", SearchOption.AllDirectories).Should().BeEmpty("nem o definitivo nem o .parcial ficam");
    }

    [Theory]
    [InlineData("../fora.bak")]
    [InlineData("a/../../b.bak")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task Copia_Rejeita_Referencia_Hostil(string referencia)
    {
        var raiz = Path.Combine(_base, "offsite");
        var acao = async () => await Copia(raiz).CopiarAsync(referencia, new MemoryStream([1]), Hash([1]));

        await acao.Should().ThrowAsync<Exception>().Where(e => e is EntradaInvalidaException || e is ArgumentException);
        Directory.Exists(Path.Combine(_base, "fora.bak")).Should().BeFalse();
        File.Exists(Path.Combine(_base, "fora.bak")).Should().BeFalse();
    }

    [Fact]
    public async Task Sem_Diretorio_Configurado_Nao_Copia_E_Remover_E_Inofensivo()
    {
        var copia = Copia(null);

        copia.Configurada.Should().BeFalse();
        await copia.RemoverAsync("tenantcompleto/aaaa/x.bak.enc");
        var acao = async () => await copia.CopiarAsync("tenantcompleto/aaaa/x.bak.enc", new MemoryStream([1]), Hash([1]));
        await acao.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- Pacote de arquivos do contribuinte ----

    private string CriarArquivo(params string[] partes)
    {
        var caminho = Path.Combine([_base, "uploads", .. partes]);
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllText(caminho, "conteudo de " + string.Join('/', partes));
        return caminho;
    }

    private EmpacotadorArquivosTenantZip Empacotador()
        => new(Options.Create(new OpcoesArmazenamento { DiretorioRaiz = Path.Combine(_base, "uploads") }));

    private static async Task<Dictionary<string, string>> LerZipAsync(MemoryStream zip)
    {
        zip.Position = 0;
        using var arquivo = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var conteudo = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entrada in arquivo.Entries)
        {
            await using var leitura = entrada.Open();
            using var texto = new StreamReader(leitura);
            conteudo[entrada.FullName] = await texto.ReadToEndAsync();
        }

        return conteudo;
    }

    [Fact]
    public async Task Pacote_Leva_So_Os_Arquivos_Do_Proprio_Contribuinte()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        CriarArquivo(a.ToString("N"), "planilha1.xlsx");
        CriarArquivo(a.ToString("N"), "sub", "planilha2.csv");
        CriarArquivo("outra-pasta", a.ToString("N"), "fora.xml");
        CriarArquivo(b.ToString("N"), "de-b.xlsx");

        using var zip = new MemoryStream();
        await Empacotador().EmpacotarAsync(a, zip);
        var conteudo = await LerZipAsync(zip);

        conteudo.Keys.Should().BeEquivalentTo(["arquivos/planilha1.xlsx", "arquivos/sub/planilha2.csv"],
            "só a pasta do contribuinte: nem outra pasta da raiz, nem a de outro contribuinte");
        conteudo["arquivos/sub/planilha2.csv"].Should().Be($"conteudo de {a:N}/sub/planilha2.csv");
        conteudo.Values.Should().NotContain(v => v.Contains(b.ToString("N"), StringComparison.Ordinal), "nada de outro contribuinte");
    }

    [Fact]
    public async Task Contribuinte_Sem_Arquivos_Gera_Pacote_Vazio_Valido()
    {
        using var zip = new MemoryStream();
        await Empacotador().EmpacotarAsync(Guid.NewGuid(), zip);

        (await LerZipAsync(zip)).Should().BeEmpty();
    }

    [Fact]
    public async Task Link_Simbolico_Para_Fora_Da_Pasta_Nao_Entra_No_Pacote()
    {
        var a = Guid.NewGuid();
        CriarArquivo(a.ToString("N"), "legitimo.xlsx");
        var segredo = Path.Combine(_base, "fora", "segredo.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(segredo)!);
        File.WriteAllText(segredo, "NAO-PODE-SAIR");
        var pasta = Path.Combine(_base, "uploads", a.ToString("N"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(pasta, "atalho.txt"), segredo);
            Directory.CreateSymbolicLink(Path.Combine(pasta, "pasta-atalho"), Path.GetDirectoryName(segredo)!);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // sem privilégio para criar links neste sistema: nada a provar aqui
        }

        using var zip = new MemoryStream();
        await Empacotador().EmpacotarAsync(a, zip);
        var conteudo = await LerZipAsync(zip);

        conteudo.Keys.Should().BeEquivalentTo(["arquivos/legitimo.xlsx"]);
        conteudo.Values.Should().NotContain("NAO-PODE-SAIR");
    }

    [Fact]
    public async Task Contribuinte_Vazio_E_Recusado()
    {
        var acao = async () => await Empacotador().EmpacotarAsync(Guid.Empty, new MemoryStream());
        await acao.Should().ThrowAsync<ArgumentException>();
    }
}
