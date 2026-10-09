using System.Text;
using FluentAssertions;

namespace PortalBase.Application.Tests.Ferramentas;

/// <summary>
/// Scripts PowerShell do repositório rodam no Windows PowerShell 5.1, que lê um <c>.ps1</c> SEM BOM na página ANSI
/// do sistema (1252 em pt-BR/en-US), não em UTF-8. Reproduzido no <c>scripts/configurar-ambiente-local.ps1</c>: o
/// travessão (UTF-8 <c>E2 80 94</c>) virou <c>â€”</c>, cujo byte <c>0x94</c> é <c>”</c> — que o PowerShell trata como
/// ASPAS — e a string fechou no meio (18 erros de parse, "UnexpectedCharactersAfterHereStringHeader"). Estes testes
/// exigem o BOM (acentos corretos) e proíbem os bytes que viram aspas em 1252 (o script continua analisável mesmo que
/// um editor remova o BOM).
/// </summary>
public sealed class ScriptsPowerShellCodificacaoTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // Em Windows-1252: 0x82 ‚ (U+201A), 0x84 „ (U+201E), 0x91 ‘, 0x92 ’, 0x93 “, 0x94 ” — o tokenizador do PowerShell
    // aceita todos como delimitadores de string.
    private static readonly HashSet<byte> BytesQueViramAspasEm1252 = [0x82, 0x84, 0x91, 0x92, 0x93, 0x94];

    [Fact]
    public void Scripts_Com_Texto_Nao_Ascii_Tem_Bom_Utf8()
    {
        var semBom = ScriptsDoRepositorio()
            .Where(s => s.Bytes.Any(b => b > 0x7F) && !s.Bytes.AsSpan().StartsWith(Bom))
            .Select(s => s.Relativo)
            .ToList();

        semBom.Should().BeEmpty("sem BOM o Windows PowerShell 5.1 decodifica o script como ANSI e corrompe o texto");
    }

    [Fact]
    public void Scripts_Nao_Contem_Bytes_Que_Viram_Aspas_Quando_Lidos_Como_Ansi()
    {
        var achados = ScriptsDoRepositorio()
            .SelectMany(s => s.Bytes
                .Select((valor, posicao) => (valor, posicao))
                .Where(x => BytesQueViramAspasEm1252.Contains(x.valor))
                .Select(x => $"{s.Relativo}: byte 0x{x.valor:X2} na posição {x.posicao} (troque o caractere por ASCII)"))
            .ToList();

        achados.Should().BeEmpty("lido em 1252 (BOM removido por um editor), o byte vira aspas e quebra o parse");
    }

    [Fact]
    public void Scripts_Sao_Utf8_Valido()
    {
        var estrito = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        foreach (var script in ScriptsDoRepositorio())
        {
            var decodificar = () => estrito.GetString(script.Bytes);
            decodificar.Should().NotThrow($"{script.Relativo} deve estar em UTF-8");
        }
    }

    private static List<(string Relativo, byte[] Bytes)> ScriptsDoRepositorio()
    {
        var raiz = LocalizarRaiz();
        var separador = Path.DirectorySeparatorChar;
        var ignorados = new[] { $"{separador}bin{separador}", $"{separador}obj{separador}", $"{separador}.git{separador}", $"{separador}node_modules{separador}" };
        var scripts = Directory.EnumerateFiles(raiz, "*.ps1", SearchOption.AllDirectories)
            .Where(caminho => !ignorados.Any(trecho => caminho.Contains(trecho, StringComparison.Ordinal)))
            .Select(caminho => (Path.GetRelativePath(raiz, caminho), File.ReadAllBytes(caminho)))
            .ToList();

        scripts.Select(s => s.Item1.Replace('\\', '/')).Should().Contain(
            "scripts/configurar-ambiente-local.ps1", "a varredura precisa enxergar os scripts reais (não pode passar vazia)");
        return scripts;
    }

    private static string LocalizarRaiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortalBase.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("a raiz do repositório (PortalBase.sln) deve ser localizável a partir do teste");
        return dir!.FullName;
    }
}
