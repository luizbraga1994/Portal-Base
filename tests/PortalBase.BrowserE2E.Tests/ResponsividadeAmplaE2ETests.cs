using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 58 — Fase 26/48 (RESPONSIVIDADE). Valida os 4 viewports exigidos (Desktop 1920, Laptop 1366,
/// Tablet 768, Mobile 390) para as páginas críticas renderizáveis sem MySQL (login + home fail-closed):
/// sem overflow horizontal da PÁGINA e com um &lt;h1&gt; presente. Estilo completo depende do host
/// publicado (o host de teste in-memory não serve os estáticos fingerprintados — limitação documentada).
/// </summary>
[Collection(ColecaoE2E.Nome)]
public sealed class ResponsividadeAmplaE2ETests
{
    private static readonly (string Nome, int L, int A)[] Viewports =
    [
        ("desktop-1920x1080", 1920, 1080),
        ("laptop-1366x768", 1366, 768),
        ("tablet-768x1024", 768, 1024),
        ("mobile-390x844", 390, 844),
    ];

    private static readonly string[] Rotas = ["/conta/entrar", "/"];

    private readonly AmbienteE2E _ambiente;

    public ResponsividadeAmplaE2ETests(AmbienteE2E ambiente) => _ambiente = ambiente;

    [SkippableFact]
    public async Task Paginas_Criticas_Sem_Overflow_Nos_Quatro_Viewports()
    {
        Skip.IfNot(_ambiente.NavegadorDisponivel,
            "BLOCKED: navegador indisponível — " + (_ambiente.MotivoBloqueioNavegador ?? "motivo desconhecido"));

        await using var contexto = await _ambiente.Navegador!.NewContextAsync(new BrowserNewContextOptions { Locale = "pt-BR" });
        var pagina = await contexto.NewPageAsync();

        foreach (var (nome, largura, altura) in Viewports)
        {
            await pagina.SetViewportSizeAsync(largura, altura);

            foreach (var rota in Rotas)
            {
                await pagina.GotoAsync(_ambiente.BaseUrl + rota, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

                var overflow = await pagina.EvaluateAsync<int>(
                    "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
                overflow.Should().BeLessThanOrEqualTo(2, $"{rota} não deve estourar a largura em {nome}");

                (await pagina.Locator("h1").CountAsync()).Should().BeGreaterThan(0, $"{rota} precisa de um cabeçalho em {nome}");
            }
        }
    }
}
