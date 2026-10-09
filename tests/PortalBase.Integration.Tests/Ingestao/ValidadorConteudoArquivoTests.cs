using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using PortalBase.Application.Ingestao;
using PortalBase.Infrastructure.Ingestao;

namespace PortalBase.Integration.Tests.Ingestao;

/// <summary>
/// Prova da validação de CONTEÚDO REAL (Prompt 08, seções 10-12): magic bytes coerentes,
/// proteção contra ZIP bomb (razão/tamanho), excesso de entradas e Zip Slip, e recusa de
/// binários disfarçados de texto/XML. Usa a implementação real de Infrastructure — sem rede/DB.
/// </summary>
public sealed class ValidadorConteudoArquivoTests
{
    private static ValidadorConteudoArquivo Criar(OpcoesUpload? opcoes = null)
        => new(Options.Create(opcoes ?? new OpcoesUpload()));

    private static MemoryStream Texto(string conteudo) => new(Encoding.UTF8.GetBytes(conteudo));

    private static MemoryStream Zip(Action<ZipArchive> montar)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            montar(zip);
        }

        ms.Position = 0;
        return ms;
    }

    private static void Entrada(ZipArchive zip, string nome, byte[] dados)
    {
        var entrada = zip.CreateEntry(nome, CompressionLevel.Optimal);
        using var s = entrada.Open();
        s.Write(dados, 0, dados.Length);
    }

    [Fact]
    public async Task Xlsx_Valido_E_Aceito()
    {
        using var ms = Zip(zip =>
        {
            Entrada(zip, "[Content_Types].xml", Encoding.UTF8.GetBytes("<Types/>"));
            Entrada(zip, "xl/workbook.xml", Encoding.UTF8.GetBytes("<workbook/>"));
        });

        var resultado = await Criar().ValidarAsync(ms, "xlsx");

        resultado.Valido.Should().BeTrue();
        resultado.ContentTypeDetectado.Should().Contain("spreadsheetml");
    }

    [Fact]
    public async Task Xlsx_Sem_ContentTypes_E_Rejeitado()
    {
        using var ms = Zip(zip => Entrada(zip, "qualquer.txt", Encoding.UTF8.GetBytes("x")));

        var resultado = await Criar().ValidarAsync(ms, "xlsx");

        resultado.Valido.Should().BeFalse();
    }

    [Fact]
    public async Task Xlsx_Com_Magic_Bytes_Errados_E_Rejeitado()
    {
        using var ms = Texto("isto nao e um zip");

        var resultado = await Criar().ValidarAsync(ms, "xlsx");

        resultado.Valido.Should().BeFalse();
    }

    [Fact]
    public async Task Zip_Com_Excesso_De_Entradas_E_Rejeitado()
    {
        var opcoes = new OpcoesUpload { MaxEntradasZip = 2 };
        using var ms = Zip(zip =>
        {
            Entrada(zip, "[Content_Types].xml", Encoding.UTF8.GetBytes("<Types/>"));
            Entrada(zip, "a.xml", Encoding.UTF8.GetBytes("<a/>"));
            Entrada(zip, "b.xml", Encoding.UTF8.GetBytes("<b/>"));
        });

        var resultado = await Criar(opcoes).ValidarAsync(ms, "xlsx");

        resultado.Valido.Should().BeFalse();
    }

    [Fact]
    public async Task Zip_Slip_E_Rejeitado()
    {
        using var ms = Zip(zip =>
        {
            Entrada(zip, "[Content_Types].xml", Encoding.UTF8.GetBytes("<Types/>"));
            Entrada(zip, "../fora.txt", Encoding.UTF8.GetBytes("escape"));
        });

        var resultado = await Criar().ValidarAsync(ms, "xlsx");

        resultado.Valido.Should().BeFalse();
    }

    [Fact]
    public async Task Zip_Bomb_Por_Razao_De_Compressao_E_Rejeitado()
    {
        var opcoes = new OpcoesUpload { RazaoMaximaCompressao = 5d };
        using var ms = Zip(zip =>
        {
            Entrada(zip, "[Content_Types].xml", Encoding.UTF8.GetBytes("<Types/>"));
            Entrada(zip, "bomba.bin", new byte[200_000]); // 200 KB de zeros → comprime muito
        });

        var resultado = await Criar(opcoes).ValidarAsync(ms, "xlsx");

        resultado.Valido.Should().BeFalse();
    }

    [Fact]
    public async Task Csv_Texto_E_Aceito()
    {
        using var ms = Texto("coluna1,coluna2\n1,2\n3,4");

        var resultado = await Criar().ValidarAsync(ms, "csv");

        resultado.Valido.Should().BeTrue();
        resultado.ContentTypeDetectado.Should().Be("text/csv");
    }

    [Fact]
    public async Task Csv_Com_Assinatura_Binaria_E_Rejeitado()
    {
        using var ms = new MemoryStream([0x50, 0x4B, 0x03, 0x04, 0x00, 0x01]); // PK.. disfarçado de csv

        var resultado = await Criar().ValidarAsync(ms, "csv");

        resultado.Valido.Should().BeFalse();
    }

    [Fact]
    public async Task Csv_Com_Byte_Nulo_E_Rejeitado()
    {
        using var ms = new MemoryStream([0x61, 0x00, 0x62, 0x63]); // 'a', NUL, 'b', 'c'

        var resultado = await Criar().ValidarAsync(ms, "csv");

        resultado.Valido.Should().BeFalse();
    }

    [Fact]
    public async Task Xml_Bem_Formado_E_Aceito()
    {
        using var ms = Texto("<raiz><item>1</item></raiz>");

        var resultado = await Criar().ValidarAsync(ms, "xml");

        resultado.Valido.Should().BeTrue();
        resultado.ContentTypeDetectado.Should().Be("application/xml");
    }

    [Fact]
    public async Task Xml_Malformado_E_Rejeitado()
    {
        using var ms = Texto("<raiz><item></raiz>");

        var resultado = await Criar().ValidarAsync(ms, "xml");

        resultado.Valido.Should().BeFalse();
    }

    [Fact]
    public async Task Xml_Que_E_Zip_E_Rejeitado()
    {
        using var ms = Zip(zip => Entrada(zip, "a.xml", Encoding.UTF8.GetBytes("<a/>")));

        var resultado = await Criar().ValidarAsync(ms, "xml");

        resultado.Valido.Should().BeFalse();
    }
}
