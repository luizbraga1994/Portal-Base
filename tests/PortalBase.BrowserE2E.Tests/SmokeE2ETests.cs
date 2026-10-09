using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 54 — smoke do pipeline E2E: prova que um navegador REAL acessa uma URL HTTP real do host
/// e recebe HTML renderizado pelo servidor. Se o navegador não puder ser lançado, o teste é
/// SKIPPED com o motivo (nunca declarado PASS sem execução — Fase 49/52).
/// </summary>
[Collection(ColecaoE2E.Nome)]
public sealed class SmokeE2ETests
{
    private readonly AmbienteE2E _ambiente;

    public SmokeE2ETests(AmbienteE2E ambiente) => _ambiente = ambiente;

    [SkippableFact]
    public async Task Navegador_Acessa_Pagina_De_Login_Real()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Navegador_Acessa_Pagina_De_Login_Real), async pagina =>
        {
            var resposta = await pagina.GotoAsync(
                _ambiente.BaseUrl + "/conta/entrar",
                new PageGotoOptions { WaitUntil = WaitUntilState.Load });

            resposta!.Status.Should().Be(200);
            await pagina.Locator("form[action='/conta/login']").WaitForAsync();
            (await pagina.Locator("#email").CountAsync()).Should().Be(1);
            (await pagina.Locator("#senha").CountAsync()).Should().Be(1);
        });
    }
}
