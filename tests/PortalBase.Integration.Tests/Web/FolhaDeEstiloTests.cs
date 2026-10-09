using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Integridade das folhas de estilo do portal. Defeito coberto: uma chave "}" sobrando no fim de um bloco
/// <c>@media</c> fez o navegador descartar a regra seguinte (<c>.auth</c>) e a tela de login perdeu o layout em duas
/// colunas — sem erro de build nem de teste. Também confere que toda fonte declarada existe no próprio portal (a CSP só
/// permite <c>font-src 'self'</c>) e vem com a sua licença.
/// </summary>
public sealed partial class FolhaDeEstiloTests
{
    [Theory]
    [InlineData("css/portalbase.css")]
    [InlineData("app.css")]
    public void Chaves_Da_Folha_De_Estilo_Estao_Balanceadas(string arquivo)
    {
        var css = SemComentariosNemTextos(File.ReadAllText(Path.Combine(Wwwroot(), arquivo)));

        var profundidade = 0;
        var linha = 1;
        foreach (var c in css)
        {
            if (c == '\n')
            {
                linha++;
            }
            else if (c == '{')
            {
                profundidade++;
            }
            else if (c == '}')
            {
                profundidade--;
                profundidade.Should().BeGreaterThanOrEqualTo(0, $"'}}' sem '{{' correspondente em {arquivo}, linha {linha}");
            }
        }

        profundidade.Should().Be(0, $"todo bloco aberto em {arquivo} precisa ser fechado");
    }

    [Fact]
    public void Regras_Da_Tela_De_Login_Ficam_No_Nivel_De_Cima()
    {
        var css = SemComentariosNemTextos(File.ReadAllText(Path.Combine(Wwwroot(), "css", "portalbase.css")));
        var inicio = css.IndexOf(".auth {", StringComparison.Ordinal);

        inicio.Should().BePositive("a regra .auth monta a tela de login em duas colunas");
        css[..inicio].Count(c => c == '{').Should().Be(css[..inicio].Count(c => c == '}'),
            "a regra .auth não pode cair dentro de outro bloco (nem ser engolida por uma chave sobrando)");
    }

    [Fact]
    public void Toda_Fonte_Declarada_Existe_No_Portal_E_Tem_Licenca()
    {
        var raiz = Wwwroot();
        var css = File.ReadAllText(Path.Combine(raiz, "css", "portalbase.css"));
        var fontes = UrlDeFonte().Matches(css).Select(m => m.Groups["arquivo"].Value).Distinct(StringComparer.Ordinal).ToList();

        fontes.Should().NotBeEmpty();
        foreach (var fonte in fontes)
        {
            File.Exists(Path.Combine(raiz, "fonts", fonte)).Should().BeTrue($"a fonte {fonte} é servida pelo próprio portal (CSP font-src 'self')");
        }

        foreach (var familia in new[] { "Archivo", "SpaceGrotesk", "IBMPlexMono" })
        {
            var licenca = Path.Combine(raiz, "fonts", $"OFL-{familia}.txt");
            File.Exists(licenca).Should().BeTrue($"a licença da fonte {familia} acompanha os arquivos");
            File.ReadAllText(licenca).Should().Contain("SIL Open Font License");
        }

        css.Should().NotContain("fonts.googleapis.com").And.NotContain("fonts.gstatic.com",
            "nenhuma fonte vem de fora (privacidade e CSP)");
    }

    private static string SemComentariosNemTextos(string css)
    {
        var saida = new StringBuilder(css.Length);
        for (var i = 0; i < css.Length; i++)
        {
            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var fim = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var trecho = fim < 0 ? css[i..] : css[i..(fim + 2)];
                saida.Append(new string('\n', trecho.Count(c => c == '\n'))); // mantém a contagem de linhas
                i = fim < 0 ? css.Length : fim + 1;
                continue;
            }

            if (css[i] is '"' or '\'')
            {
                var aspas = css[i];
                var fim = css.IndexOf(aspas, i + 1);
                i = fim < 0 ? css.Length : fim;
                continue;
            }

            saida.Append(css[i]);
        }

        return saida.ToString();
    }

    private static string Wwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortalBase.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("a raiz do repositório (PortalBase.sln) deve ser localizável");
        return Path.Combine(dir!.FullName, "src", "PortalBase.Web", "wwwroot");
    }

    [GeneratedRegex("""url\("\.\./fonts/(?<arquivo>[A-Za-z0-9._-]+\.woff2)"\)""", RegexOptions.CultureInvariant)]
    private static partial Regex UrlDeFonte();
}
