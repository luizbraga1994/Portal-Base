using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Ingestao;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Integration.Tests.Ingestao;

/// <summary>
/// Prova do armazenamento em DIRETÓRIO PRIVADO real (Prompt 08, seções 13, 15, 26): roundtrip
/// de gravação/leitura/remoção por streaming, ISOLAMENTO por subpasta de tenant e recusa de
/// PATH TRAVERSAL (o caminho nunca escapa da raiz). Usa filesystem temporário — limpo ao final.
/// </summary>
public sealed class ArmazenamentoArquivosFilesystemTests : IDisposable
{
    private readonly string _raiz;
    private readonly ArmazenamentoArquivosFilesystem _armazenamento;

    public ArmazenamentoArquivosFilesystemTests()
    {
        _raiz = Path.Combine(Path.GetTempPath(), "portalbase-upl-" + Guid.NewGuid().ToString("N"));
        _armazenamento = new ArmazenamentoArquivosFilesystem(
            Options.Create(new OpcoesArmazenamento { DiretorioRaiz = _raiz }));
    }

    private static MemoryStream Bytes(string conteudo) => new(Encoding.UTF8.GetBytes(conteudo));

    [Fact]
    public async Task Salvar_E_Abrir_Faz_Roundtrip()
    {
        var referencia = await _armazenamento.SalvarAsync("t1/arquivo.csv", Bytes("linha1"), CancellationToken.None);

        referencia.Should().Be("t1/arquivo.csv");

        await using var leitura = await _armazenamento.AbrirLeituraAsync(referencia, CancellationToken.None);
        using var buffer = new MemoryStream();
        await leitura.CopyToAsync(buffer);
        Encoding.UTF8.GetString(buffer.ToArray()).Should().Be("linha1");
    }

    [Fact]
    public async Task Tenants_Distintos_Ficam_Em_Subpastas_Separadas()
    {
        await _armazenamento.SalvarAsync("tenantA/f.csv", Bytes("A"), CancellationToken.None);
        await _armazenamento.SalvarAsync("tenantB/f.csv", Bytes("B"), CancellationToken.None);

        File.Exists(Path.Combine(_raiz, "tenantA", "f.csv")).Should().BeTrue();
        File.Exists(Path.Combine(_raiz, "tenantB", "f.csv")).Should().BeTrue();

        await using var leituraA = await _armazenamento.AbrirLeituraAsync("tenantA/f.csv", CancellationToken.None);
        using var buffer = new MemoryStream();
        await leituraA.CopyToAsync(buffer);
        Encoding.UTF8.GetString(buffer.ToArray()).Should().Be("A"); // não vaza o de B
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("t1/../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("t1/../../../etc/passwd")]
    public async Task Path_Traversal_E_Rejeitado(string caminho)
    {
        var acao = async () => await _armazenamento.SalvarAsync(caminho, Bytes("x"), CancellationToken.None);

        await acao.Should().ThrowAsync<EntradaInvalidaException>();
    }

    [Fact]
    public async Task Remover_Apaga_O_Arquivo()
    {
        var referencia = await _armazenamento.SalvarAsync("t1/apagar.csv", Bytes("x"), CancellationToken.None);
        File.Exists(Path.Combine(_raiz, "t1", "apagar.csv")).Should().BeTrue();

        await _armazenamento.RemoverAsync(referencia, CancellationToken.None);

        File.Exists(Path.Combine(_raiz, "t1", "apagar.csv")).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz))
        {
            Directory.Delete(_raiz, recursive: true);
        }
    }
}
