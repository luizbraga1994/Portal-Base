using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 54 — Fase 33 (RESPONSIVIDADE), escopo mínimo (a auditoria visual completa é o Prompt 58).
/// Valida que login e home não geram overflow horizontal da página em viewport Desktop e Mobile — o
/// conteúdo cabe na largura da janela (scroll interno de tabela em <c>.dt-scroll</c> é aceitável).
/// </summary>
[Collection(ColecaoE2E.Nome)]
public sealed class ResponsividadeE2ETests
{
    private static readonly (string Nome, int L, int A)[] Viewports =
    [
        ("desktop", 1280, 800),
        ("mobile", 390, 844),
    ];

    private static readonly string[] Rotas = ["/conta/entrar", "/"];

    private readonly AmbienteE2E _ambiente;

    public ResponsividadeE2ETests(AmbienteE2E ambiente) => _ambiente = ambiente;

    [SkippableFact]
    public async Task Paginas_Nao_Tem_Overflow_Horizontal_Em_Desktop_E_Mobile()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Paginas_Nao_Tem_Overflow_Horizontal_Em_Desktop_E_Mobile), async pagina =>
        {
            foreach (var (nome, largura, altura) in Viewports)
            {
                await pagina.SetViewportSizeAsync(largura, altura);

                foreach (var rota in Rotas)
                {
                    await pagina.GotoAsync(_ambiente.BaseUrl + rota, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

                    // Overflow horizontal da PÁGINA (documentElement) — tolera 2px de arredondamento.
                    var overflow = await pagina.EvaluateAsync<int>(
                        "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");

                    overflow.Should().BeLessThanOrEqualTo(2,
                        $"a rota {rota} não deve estourar a largura em {nome} ({largura}px)");
                }
            }
        });
    }
}
