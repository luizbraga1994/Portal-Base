using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 54 — Fase 7 (AUTHENTICATION) validada pelo NAVEGADOR no host sem banco: a página de login real
/// renderiza, um login inválido NÃO estabelece sessão e não vaza detalhe, e a home sem sessão é fail-closed
/// (redireciona ao login). P62-R1: o login VÁLIDO/sessão/logout e a sessão expirada — antes placeholders que
/// lançavam com MySQL — são testes REAIS em <see cref="FluxosAutenticadosIntegradoE2ETests"/> (usuário semeado).
/// </summary>
[Collection(ColecaoE2E.Nome)]
public sealed class AutenticacaoE2ETests
{
    private readonly AmbienteE2E _ambiente;

    public AutenticacaoE2ETests(AmbienteE2E ambiente) => _ambiente = ambiente;

    [SkippableFact] // §7.1/§7.2 — a página de login real renderiza com o formulário do servidor
    public async Task Pagina_De_Login_Renderiza_Formulario()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Pagina_De_Login_Renderiza_Formulario), async pagina =>
        {
            var resposta = await Ir(pagina, "/conta/entrar");
            resposta!.Status.Should().Be(200);

            await pagina.Locator("form[action='/conta/login']").WaitForAsync();
            (await pagina.Locator("input#email[type='email']").CountAsync()).Should().Be(1, "o login é pelo e-mail");
            (await pagina.Locator("input#senha[type='password']").CountAsync()).Should().Be(1);
            (await pagina.Locator("button[type='submit']").CountAsync()).Should().BeGreaterThan(0);

            AfirmacoesSeguranca.DeveEstarLivreDeVazamento(await pagina.ContentAsync(), "login");
        });
    }

    [SkippableFact] // Layout: marca à esquerda e formulário à direita no computador; só o formulário no celular
    public async Task Pagina_De_Login_Fica_Em_Duas_Colunas_No_Computador_E_Usa_As_Fontes_Do_Portal()
    {
        // Defeito coberto: uma chave "}" sobrando no CSS descartava a regra .auth e a marca ficava EM CIMA do formulário.
        await _ambiente.ExecutarNavegadorAsync(nameof(Pagina_De_Login_Fica_Em_Duas_Colunas_No_Computador_E_Usa_As_Fontes_Do_Portal), async pagina =>
        {
            await pagina.SetViewportSizeAsync(1440, 900);
            await Ir(pagina, "/conta/entrar");
            var marca = await pagina.Locator(".auth__brand").BoundingBoxAsync();
            var painel = await pagina.Locator(".auth__panel").BoundingBoxAsync();
            marca.Should().NotBeNull();
            painel.Should().NotBeNull();
            (marca!.X + marca.Width).Should().BeLessThanOrEqualTo(painel!.X + 1, "a marca fica à esquerda do formulário");
            marca.Y.Should().BeApproximately(painel.Y, 1, "lado a lado, na mesma linha");

            // As fontes vêm do próprio portal (CSP font-src 'self') e carregam de verdade.
            (await pagina.EvaluateAsync<bool>(
                "async () => { const faces = await document.fonts.load('700 24px \"Space Grotesk\"'); return faces.length > 0 && faces.every(f => f.status === 'loaded'); }"))
                .Should().BeTrue("a fonte dos títulos é servida pelo portal");

            await pagina.SetViewportSizeAsync(390, 844);
            (await pagina.Locator(".auth__brand").IsVisibleAsync()).Should().BeFalse("no celular só o formulário aparece");
            (await pagina.Locator("input#email").IsVisibleAsync()).Should().BeTrue();
        });
    }

    [SkippableFact]
    public async Task Link_Esqueci_Minha_Senha_Fica_Ao_Lado_Do_Campo_Senha_E_Visivel_Nos_Dois_Temas()
    {
        // Defeito coberto: o link ficava pequeno e apagado embaixo do botão Entrar — o usuário não o encontrava.
        await _ambiente.ExecutarNavegadorAsync(nameof(Link_Esqueci_Minha_Senha_Fica_Ao_Lado_Do_Campo_Senha_E_Visivel_Nos_Dois_Temas), async pagina =>
        {
            await pagina.SetViewportSizeAsync(1440, 900);
            foreach (var tema in new[] { ColorScheme.Light, ColorScheme.Dark })
            {
                await pagina.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = tema });
                await Ir(pagina, "/conta/entrar");
                var link = pagina.GetByRole(AriaRole.Link, new() { Name = "Esqueci minha senha" });
                (await link.CountAsync()).Should().Be(1);
                (await link.IsVisibleAsync()).Should().BeTrue();

                var rotulo = await pagina.Locator("label[for='senha']").BoundingBoxAsync();
                var caixa = await link.BoundingBoxAsync();
                var campo = await pagina.Locator("input#senha").BoundingBoxAsync();
                caixa!.Y.Should().BeApproximately(rotulo!.Y, 4, "na mesma linha do rótulo Senha");
                (caixa.X + caixa.Width).Should().BeApproximately(campo!.X + campo.Width, 2, "alinhado à direita do campo");
                (await link.EvaluateAsync<string>("e => getComputedStyle(e).fontWeight")).Should().Be("700", "em destaque");
            }

            await pagina.GetByRole(AriaRole.Link, new() { Name = "Esqueci minha senha" }).ClickAsync();
            await pagina.WaitForURLAsync("**/conta/esqueci-senha");
        });
    }

    [SkippableFact] // §7.4 — credenciais inválidas NÃO criam sessão e a resposta é segura (sem vazamento)
    public async Task Login_Invalido_Nao_Cria_Sessao()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Login_Invalido_Nao_Cria_Sessao), async pagina =>
        {
            await Ir(pagina, "/conta/entrar");
            await pagina.FillAsync("input#email", "inexistente@exemplo.com");
            await pagina.FillAsync("input#senha", "SenhaErrada!" + Guid.NewGuid().ToString("N"));
            await pagina.ClickAsync("button[type='submit']");
            await pagina.WaitForLoadStateAsync(LoadState.Load);

            // Resposta ao POST inválido não pode vazar stack/credencial (seja erro=1 ou página de erro).
            AfirmacoesSeguranca.DeveEstarLivreDeVazamento(await pagina.ContentAsync(), "pos-login-invalido");

            // Prova de que NENHUMA sessão foi estabelecida: sem autenticação a home redireciona
            // para o login (um usuário autenticado NÃO seria redirecionado).
            await Ir(pagina, "/");
            pagina.Url.Should().Contain("/conta/entrar", "um login inválido não pode autenticar o usuário");
            (await pagina.Locator("form[action='/conta/login']").CountAsync())
                .Should().BeGreaterThan(0, "sem sessão, a home apresenta o formulário de login");
        });
    }

    [SkippableFact] // §7 (home) — sem sessão, o Dashboard é fail-closed (não exibe dado de tenant)
    public async Task Home_Sem_Sessao_E_FailClosed()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Home_Sem_Sessao_E_FailClosed), async pagina =>
        {
            var resposta = await Ir(pagina, "/");
            resposta!.Status.Should().Be(200);

            // Estado seguro esperado: boas-vindas / sem permissão / selecionar contribuinte — nunca dados.
            var texto = await pagina.InnerTextAsync("body");
            texto.Should().ContainAny("Bem-vindo", "Sem permissão", "Selecione um contribuinte", "Entrar");

            AfirmacoesSeguranca.DeveEstarLivreDeVazamento(await pagina.ContentAsync(), "home");
        });
    }

    private async Task<IResponse?> Ir(IPage pagina, string rota) =>
        await pagina.GotoAsync(_ambiente.BaseUrl + rota, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
}
