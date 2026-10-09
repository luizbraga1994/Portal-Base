using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using PortalBase.Web.Components;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// UI-001 (auditoria de 08/10/2026), guarda permanente: com o prerender, cada navegação completa carregava os dados da
/// página DUAS vezes (no render do servidor e de novo quando o circuito conectava). As páginas usam o modo interativo
/// SEM prerender — uma página nova com <c>@rendermode InteractiveServer</c> traria a carga dupla de volta.
/// </summary>
public sealed class ModosRenderizacaoTests
{
    [Fact]
    public void Modo_Das_Paginas_E_Interativo_No_Servidor_Sem_Prerender()
    {
        var modo = ModosRenderizacao.InterativoSemPrerender.Should().BeOfType<InteractiveServerRenderMode>().Subject;
        modo.Prerender.Should().BeFalse();
    }

    [Fact]
    public void Nenhuma_Pagina_Usa_O_Modo_Com_Prerender()
    {
        var paginas = Path.Combine(RaizRepositorio(), "src", "PortalBase.Web", "Components", "Pages");
        var arquivos = Directory.EnumerateFiles(paginas, "*.razor", SearchOption.AllDirectories).ToList();
        arquivos.Should().NotBeEmpty();

        var interativas = 0;
        var violacoes = new List<string>();
        foreach (var arquivo in arquivos)
        {
            var linhas = File.ReadAllLines(arquivo);
            var diretiva = linhas.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("@rendermode", StringComparison.Ordinal));
            if (diretiva is null)
            {
                continue; // página estática (SSR): sem circuito
            }

            interativas++;
            if (!string.Equals(diretiva, "@rendermode InterativoSemPrerender", StringComparison.Ordinal))
            {
                violacoes.Add($"{Path.GetFileName(arquivo)}: {diretiva}");
            }
        }

        interativas.Should().BeGreaterThan(15, "as telas do portal são interativas");
        violacoes.Should().BeEmpty("toda página interativa usa @rendermode InterativoSemPrerender (UI-001)");
    }

    private static string RaizRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
