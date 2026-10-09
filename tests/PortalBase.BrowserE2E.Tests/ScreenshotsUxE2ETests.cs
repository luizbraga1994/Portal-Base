using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 58 — captura de screenshots dos fluxos críticos (Fase 47/48) em desktop e mobile, para
/// auditoria visual e regressão visual seletiva (não centenas de snapshots frágeis). Estado sem
/// MySQL = fail-closed (shell + boas-vindas/estados). Artefatos em artifacts/e2e/ux (gitignored).
/// </summary>
[Collection(ColecaoE2E.Nome)]
public sealed class ScreenshotsUxE2ETests
{
    private static readonly (string Nome, int L, int A)[] Viewports =
    [
        ("desktop-1920", 1920, 1080),
        ("mobile-390", 390, 844),
    ];

    private static readonly (string Nome, string Rota)[] Paginas =
    [
        ("login", "/conta/entrar"),
        ("home", "/"),
    ];

    private readonly AmbienteE2E _ambiente;

    public ScreenshotsUxE2ETests(AmbienteE2E ambiente) => _ambiente = ambiente;

    [SkippableFact]
    public async Task Capturar_Fluxos_Criticos_Desktop_E_Mobile()
    {
        Skip.IfNot(_ambiente.NavegadorDisponivel,
            "BLOCKED: navegador indisponível — " + (_ambiente.MotivoBloqueioNavegador ?? "motivo desconhecido"));

        var dir = Path.Combine(_ambiente.DiretorioEvidencias, "ux");
        Directory.CreateDirectory(dir);

        foreach (var (vpNome, largura, altura) in Viewports)
        {
            await using var contexto = await _ambiente.Navegador!.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = largura, Height = altura },
                Locale = "pt-BR",
            });
            var pagina = await contexto.NewPageAsync();

            foreach (var (nome, rota) in Paginas)
            {
                await pagina.GotoAsync(_ambiente.BaseUrl + rota, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await pagina.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(dir, $"{nome}-{vpNome}.png"),
                    FullPage = true,
                });
            }
        }
    }
}
