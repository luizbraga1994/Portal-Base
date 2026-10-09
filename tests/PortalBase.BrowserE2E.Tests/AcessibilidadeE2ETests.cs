using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 54 — Fase 34 (ACESSIBILIDADE BÁSICA), sem virar auditoria WCAG completa. Valida rótulos
/// associados, nome acessível do botão, atributo de idioma e navegação por teclado (ordem de foco)
/// no formulário de login — a superfície de acesso mais crítica.
/// </summary>
[Collection(ColecaoE2E.Nome)]
public sealed class AcessibilidadeE2ETests
{
    private readonly AmbienteE2E _ambiente;

    public AcessibilidadeE2ETests(AmbienteE2E ambiente) => _ambiente = ambiente;

    [SkippableFact] // rótulos associados (label[for] ↔ input id) e nome acessível do botão
    public async Task Login_Tem_Rotulos_Associados_E_Botao_Nomeado()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Login_Tem_Rotulos_Associados_E_Botao_Nomeado), async pagina =>
        {
            await pagina.GotoAsync(_ambiente.BaseUrl + "/conta/entrar", new PageGotoOptions { WaitUntil = WaitUntilState.Load });

            (await pagina.Locator("label[for='email']").CountAsync()).Should().Be(1);
            (await pagina.Locator("label[for='senha']").CountAsync()).Should().Be(1);

            var textoBotao = (await pagina.Locator("button[type='submit']").InnerTextAsync()).Trim();
            textoBotao.Should().NotBeNullOrWhiteSpace("o botão de submit precisa de nome acessível");

            var lang = await pagina.GetAttributeAsync("html", "lang");
            lang.Should().NotBeNullOrWhiteSpace("a página deve declarar o idioma (atributo lang)");
        });
    }

    [SkippableFact] // navegação por teclado: o foco alcança e-mail e senha na ordem esperada
    public async Task Login_Navegavel_Por_Teclado()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Login_Navegavel_Por_Teclado), async pagina =>
        {
            await pagina.GotoAsync(_ambiente.BaseUrl + "/conta/entrar", new PageGotoOptions { WaitUntil = WaitUntilState.Load });

            await pagina.Locator("#email").FocusAsync();
            (await pagina.EvaluateAsync<string>("() => document.activeElement?.id")).Should().Be("email");

            await pagina.Keyboard.PressAsync("Tab");
            (await pagina.EvaluateAsync<string>("() => document.activeElement?.id")).Should().Be("senha");
        });
    }
}
