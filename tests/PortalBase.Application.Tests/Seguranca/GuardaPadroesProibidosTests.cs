using System.Text.RegularExpressions;
using FluentAssertions;

namespace PortalBase.Application.Tests.Seguranca;

/// <summary>
/// SAST-lite / regressão de segurança (Prompt 30, seções 22, 61, 76) — varre o código de PRODUÇÃO (src/**.cs)
/// procurando padrões PROIBIDOS e FALHA o teste se algum aparecer. É a rede de segurança automatizada que
/// impede que um controle já implantado (P04-P29) seja removido: SQL bruto, bypass do filtro de tenant, endpoint
/// anônimo, escrita em console (vazamento por log), e tempo não-determinístico/não-UTC (§8).
/// </summary>
public sealed class GuardaPadroesProibidosTests
{
    private static readonly (string Nome, Regex Padrao)[] Proibidos =
    [
        ("Console.Write* (vazamento por log — §22)", new Regex(@"\bConsole\.(Write|WriteLine|Error)\b", RegexOptions.Compiled)),
        ("SQL bruto FromSqlRaw (injeção — §19)", new Regex(@"\bFromSqlRaw\b", RegexOptions.Compiled)),
        ("SQL bruto ExecuteSqlRaw (injeção — §19)", new Regex(@"\bExecuteSqlRaw\b", RegexOptions.Compiled)),
        ("IgnoreQueryFilters (bypass do filtro de tenant — §6)", new Regex(@"\bIgnoreQueryFilters\b", RegexOptions.Compiled)),
        ("AllowAnonymous (bypass de autorização — §20)", new Regex(@"\bAllowAnonymous\b", RegexOptions.Compiled)),
        ("DateTime.Now / .Today (não-determinístico/não-UTC — §8)", new Regex(@"\bDateTime\.(Now|Today)\b", RegexOptions.Compiled)),
        ("DateTimeOffset.Now (não-UTC — §8)", new Regex(@"\bDateTimeOffset\.Now\b", RegexOptions.Compiled)),
    ];

    [Fact]
    public void CodigoDeProducao_NaoContemPadroesProibidos()
    {
        var src = LocalizarSrc();
        var arquivos = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(caminho => !caminho.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !caminho.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var achados = new List<string>();
        foreach (var arquivo in arquivos)
        {
            // Únicas exceções no src: os comandos de servidor da Web (Web/Hospedagem) — "criar-primeiro-administrador" e
            // "decifrar-backup" (recuperação de desastre) —, cuja interface com quem administra o servidor É o console
            // (senha lida sem eco, nunca escrita; sem segredo nem detalhe interno). Lista fechada, arquivo por arquivo.
            var comandoDeImplantacao =
                arquivo.EndsWith(Path.Combine("PortalBase.Web", "Hospedagem", "ComandoPrimeiroAdministrador.cs"), StringComparison.Ordinal)
                || arquivo.EndsWith(Path.Combine("PortalBase.Web", "Hospedagem", "ComandoDecifrarBackup.cs"), StringComparison.Ordinal);
            var linhas = File.ReadAllLines(arquivo);
            for (var i = 0; i < linhas.Length; i++)
            {
                foreach (var (nome, padrao) in Proibidos)
                {
                    if (padrao.IsMatch(linhas[i]) && !(comandoDeImplantacao && nome.StartsWith("Console", StringComparison.Ordinal)))
                    {
                        achados.Add($"{Path.GetFileName(arquivo)}:{i + 1} — {nome}");
                    }
                }
            }
        }

        achados.Should().BeEmpty("nenhum padrão proibido deve existir no código de produção");
    }

    /// <summary>
    /// As ferramentas de desenvolvimento (tools/**) seguem as MESMAS proibições. Única exceção: o terminal do comando
    /// de acesso local, cuja interface com o usuário É o console (senha lida sem eco, nunca escrita).
    /// </summary>
    [Fact]
    public void Ferramentas_NaoContemPadroesProibidos_SalvoOTerminalDoComandoDeAcessoLocal()
    {
        var tools = Path.Combine(Directory.GetParent(LocalizarSrc())!.FullName, "tools");
        var arquivos = Directory.EnumerateFiles(tools, "*.cs", SearchOption.AllDirectories)
            .Where(caminho => !caminho.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !caminho.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        arquivos.Should().NotBeEmpty("a varredura precisa enxergar o código das ferramentas");

        var achados = new List<string>();
        foreach (var arquivo in arquivos)
        {
            var terminal = Path.GetFileName(arquivo) == "TerminalConsole.cs";
            var linhas = File.ReadAllLines(arquivo);
            for (var i = 0; i < linhas.Length; i++)
            {
                foreach (var (nome, padrao) in Proibidos)
                {
                    if (padrao.IsMatch(linhas[i]) && !(terminal && nome.StartsWith("Console", StringComparison.Ordinal)))
                    {
                        achados.Add($"{Path.GetFileName(arquivo)}:{i + 1} — {nome}");
                    }
                }
            }
        }

        achados.Should().BeEmpty();
    }

    /// <summary>A ferramenta de acesso local não pode virar dependência de produção nem ser publicada nas imagens.</summary>
    [Fact]
    public void FerramentaDeAcessoLocal_NaoEReferenciadaPorProducao_NemPublicadaNasImagens()
    {
        var src = LocalizarSrc();
        var raiz = Directory.GetParent(src)!.FullName;

        var referencias = Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(csproj => File.ReadAllText(csproj).Contains("AcessoLocal", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName);
        referencias.Should().BeEmpty("código de produção não depende da ferramenta de desenvolvimento");

        foreach (var dockerfile in Directory.EnumerateFiles(raiz, "Dockerfile*", SearchOption.TopDirectoryOnly))
        {
            File.ReadAllText(dockerfile).Should().NotContain("AcessoLocal", $"{Path.GetFileName(dockerfile)} não publica a ferramenta");
        }
    }

    private static string LocalizarSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortalBase.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("a raiz do repositório (PortalBase.sln) deve ser localizável a partir do teste");
        var src = Path.Combine(dir!.FullName, "src");
        Directory.Exists(src).Should().BeTrue();
        return src;
    }
}
