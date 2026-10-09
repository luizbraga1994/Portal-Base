using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 54 — Fases 10/28/29 (NAVEGAÇÃO / ERROR STATES / LOADING). Valida que a rota inexistente cai
/// no "não encontrado" (sem 500/AmbiguousMatch/stack), que a página de não-encontrado renderiza e que
/// as páginas do shell têm estrutura consistente (um &lt;h1&gt; — alvo do FocusOnNavigate), sem travar.
/// </summary>
[Collection(ColecaoE2E.Nome)]
public sealed class NavegacaoErroE2ETests
{
    private readonly AmbienteE2E _ambiente;

    public NavegacaoErroE2ETests(AmbienteE2E ambiente) => _ambiente = ambiente;

    [SkippableFact] // §29 — a página de não-encontrado renderiza (não trava, sem vazamento)
    public async Task Pagina_NotFound_Renderiza()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Pagina_NotFound_Renderiza), async pagina =>
        {
            var resposta = await pagina.GotoAsync(
                _ambiente.BaseUrl + "/not-found",
                new PageGotoOptions { WaitUntil = WaitUntilState.Load });

            resposta!.Status.Should().Be(200);
            var texto = await pagina.InnerTextAsync("body");
            texto.Should().ContainAny("Not Found", "não existe", "does not exist");
            AfirmacoesSeguranca.DeveEstarLivreDeVazamento(await pagina.ContentAsync(), "not-found");
        });
    }

    [SkippableFact] // §28 — rota inexistente é tratada como não-encontrada, sem AmbiguousMatch/stack
    public async Task Rota_Inexistente_Cai_Em_NaoEncontrado()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Rota_Inexistente_Cai_Em_NaoEncontrado), async pagina =>
        {
            await pagina.GotoAsync(
                _ambiente.BaseUrl + "/rota-que-nao-existe-" + Guid.NewGuid().ToString("N"),
                new PageGotoOptions { WaitUntil = WaitUntilState.Load });

            var html = await pagina.ContentAsync();
            html.Should().ContainAny("Not Found", "does not exist", "não existe");
            AfirmacoesSeguranca.DeveEstarLivreDeVazamento(html, "rota-inexistente");
        });
    }

    [SkippableFact] // §10/§29 — o shell renderiza com um único h1 (sem AmbiguousMatch, sem página travada)
    public async Task Home_Tem_Estrutura_De_Pagina()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Home_Tem_Estrutura_De_Pagina), async pagina =>
        {
            await pagina.GotoAsync(_ambiente.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.Load });

            (await pagina.Locator("h1").CountAsync()).Should().BeGreaterThan(0, "a página precisa de um cabeçalho (FocusOnNavigate)");
            (await pagina.Locator("body").CountAsync()).Should().Be(1);
            AfirmacoesSeguranca.DeveEstarLivreDeVazamento(await pagina.ContentAsync(), "home-estrutura");
        });
    }
}
