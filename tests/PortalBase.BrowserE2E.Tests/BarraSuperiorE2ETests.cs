using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Botões da barra superior no organismo integrado (Browser → Web/Kestrel → MySQL real), com login real. O layout é
/// renderizado no SERVIDOR (estático): "Recolher menu", "Menu" (gaveta do celular) e "Alternar tema" não podiam usar
/// @onclick — o clique nunca chegava a lugar nenhum. Agora são tratados por wwwroot/js/portalbase.js (data-ui); estes
/// testes clicam de verdade e conferem o efeito, inclusive depois da navegação aprimorada, que reescreve o DOM.
/// Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED (skip explicado).
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class BarraSuperiorE2ETests
{
    private const string Shell = ".app-shell";
    private static readonly PageWaitForFunctionOptions Espera = new() { Timeout = 15_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public BarraSuperiorE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Recolher_Menu_Funciona_E_Sobrevive_A_Navegacao_E_Ao_Recarregar()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        var recolher = pagina.Locator("[data-ui='recolher-nav']");

        (await TemClasseAsync(pagina, "collapsed")).Should().BeFalse("o menu começa expandido");
        var larguraExpandida = await LarguraMenuAsync(pagina);

        await recolher.ClickAsync();
        await AguardarClasseAsync(pagina, "collapsed", presente: true);
        (await recolher.GetAttributeAsync("aria-pressed")).Should().Be("true");
        // Recolhido, o menu mostra só os ícones (espera a transição de largura do CSS terminar).
        await pagina.WaitForFunctionAsync(
            $"() => document.querySelector('.sidebar').getBoundingClientRect().width < {larguraExpandida.ToString(System.Globalization.CultureInfo.InvariantCulture)} - 100",
            null,
            Espera);

        // Navegação aprimorada (clique num item do menu): o DOM vem de novo do servidor e a preferência é reaplicada.
        await NavegarPeloMenuAsync(pagina);
        await AguardarClasseAsync(pagina, "collapsed", presente: true);

        // Página recarregada inteira: continua recolhido (preferência do navegador).
        await pagina.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load });
        await AguardarClasseAsync(pagina, "collapsed", presente: true);

        await pagina.Locator("[data-ui='recolher-nav']").ClickAsync();
        await AguardarClasseAsync(pagina, "collapsed", presente: false);
        (await pagina.Locator("[data-ui='recolher-nav']").GetAttributeAsync("aria-pressed")).Should().Be("false");
    }

    [SkippableFact]
    public async Task Alternar_Tema_Troca_O_Tema_Efetivo_E_O_Icone_E_Persiste()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;

        var inicial = await TemaEfetivoAsync(pagina);
        var esperado = inicial == "dark" ? "light" : "dark";
        await IconeDeveCorresponderAsync(pagina, inicial);

        // 1º clique sempre MUDA o que se vê (vai para o oposto do tema efetivo, mesmo quando ele vem do sistema).
        await pagina.Locator("[data-ui='alternar-tema']").ClickAsync();
        (await pagina.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme')")).Should().Be(esperado);
        (await TemaEfetivoAsync(pagina)).Should().Be(esperado);
        await IconeDeveCorresponderAsync(pagina, esperado);
        var fundo = await FundoAsync(pagina);

        // Persiste na navegação aprimorada e ao recarregar.
        await NavegarPeloMenuAsync(pagina);
        await pagina.WaitForFunctionAsync($"() => document.documentElement.getAttribute('data-theme') === '{esperado}'", null, Espera);
        await pagina.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load });
        (await TemaEfetivoAsync(pagina)).Should().Be(esperado);
        (await FundoAsync(pagina)).Should().Be(fundo, "as cores do tema escolhido continuam aplicadas");

        // 2º clique volta.
        await pagina.Locator("[data-ui='alternar-tema']").ClickAsync();
        (await TemaEfetivoAsync(pagina)).Should().Be(inicial);
        await IconeDeveCorresponderAsync(pagina, inicial);
    }

    [SkippableFact]
    public async Task Menu_Do_Celular_Abre_E_Fecha()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        await pagina.SetViewportSizeAsync(390, 844);
        var menu = pagina.Locator("[data-ui='alternar-nav']");

        (await menu.IsVisibleAsync()).Should().BeTrue("no celular o botão de menu aparece");
        // A gaveta começa fechada (fora da tela; espera a transição de .2s depois de mudar o tamanho da janela).
        await pagina.WaitForFunctionAsync("() => document.querySelector('.sidebar').getBoundingClientRect().right <= 0", null, Espera);
        (await TemClasseAsync(pagina, "nav-open")).Should().BeFalse();

        await menu.ClickAsync();
        await AguardarClasseAsync(pagina, "nav-open", presente: true);
        (await menu.GetAttributeAsync("aria-expanded")).Should().Be("true");
        await pagina.WaitForFunctionAsync("() => document.querySelector('.sidebar').getBoundingClientRect().left >= 0", null, Espera);

        // Tocar fora (fundo escurecido) fecha.
        await pagina.Locator("[data-ui='fechar-nav']").ClickAsync(new LocatorClickOptions { Position = new Position { X = 370, Y = 400 } });
        await AguardarClasseAsync(pagina, "nav-open", presente: false);
        (await menu.GetAttributeAsync("aria-expanded")).Should().Be("false");

        // Escolher um item fecha a gaveta e abre a página.
        await menu.ClickAsync();
        await AguardarClasseAsync(pagina, "nav-open", presente: true);
        await NavegarPeloMenuAsync(pagina);
        await AguardarClasseAsync(pagina, "nav-open", presente: false);

        // Esc também fecha.
        await pagina.Locator("[data-ui='alternar-nav']").ClickAsync();
        await AguardarClasseAsync(pagina, "nav-open", presente: true);
        await pagina.Keyboard.PressAsync("Escape");
        await AguardarClasseAsync(pagina, "nav-open", presente: false);
    }

    [SkippableFact]
    public async Task Menu_Do_Usuario_Abre_Com_Alterar_Senha_E_Sair()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        var botao = pagina.Locator("button.user-btn");
        var menu = pagina.Locator("#menu-usuario");

        // Fechado ao carregar; o clique no nome abre com as duas opções.
        (await menu.IsVisibleAsync()).Should().BeFalse("o menu do usuário começa fechado");
        (await botao.GetAttributeAsync("aria-expanded")).Should().Be("false");
        await botao.ClickAsync();
        await AguardarMenuUsuarioAsync(pagina, visivel: true);
        (await botao.GetAttributeAsync("aria-expanded")).Should().Be("true");
        (await menu.Locator(".user-dropdown__item").AllInnerTextsAsync())
            .Select(t => string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Should().Equal(["Alterar senha", "Sair"]);

        // Esc fecha e devolve o foco ao botão; clicar fora também fecha.
        await pagina.Keyboard.PressAsync("Escape");
        await AguardarMenuUsuarioAsync(pagina, visivel: false);
        (await pagina.EvaluateAsync<string>("() => document.activeElement && document.activeElement.id")).Should().Be("btn-menu-usuario");
        await botao.ClickAsync();
        await AguardarMenuUsuarioAsync(pagina, visivel: true);
        await pagina.Locator("main h1").First.ClickAsync(); // área neutra da página
        await AguardarMenuUsuarioAsync(pagina, visivel: false);

        // "Alterar senha" leva ao formulário de troca de senha (Minha conta), já fechado na página seguinte.
        await botao.ClickAsync();
        await pagina.Locator("#menu-usuario-senha").ClickAsync();
        await pagina.WaitForURLAsync("**/conta/minha-conta#alterar-senha", new PageWaitForURLOptions { Timeout = 30_000 });
        await pagina.Locator("input#senhaAtual").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // "Sair" encerra a sessão em um clique: volta ao login e o portal deixa de abrir.
        await pagina.GotoAsync("/", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await pagina.Locator("button.user-btn").ClickAsync();
        await AguardarMenuUsuarioAsync(pagina, visivel: true);
        await pagina.Locator("#menu-usuario-sair").ClickAsync();
        await pagina.WaitForURLAsync(url => url.Contains("/conta/entrar", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });
        await pagina.GotoAsync("/admin/usuarios", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        pagina.Url.Should().Contain("/conta/entrar", "depois de sair, as páginas do portal pedem login de novo");
    }

    [SkippableFact]
    public async Task Conteudo_Rolado_Some_Atras_Da_Faixa_Da_Barra_Superior()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        await pagina.SetViewportSizeAsync(1280, 720);
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync("/admin/permissoes", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        await pagina.Locator("main table").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // Rolada a página, o vão acima da barra flutuante e o logo abaixo dela não mostram o conteúdo (cartões/tabelas).
        await pagina.EvaluateAsync("() => window.scrollTo(0, 600)");
        await pagina.WaitForFunctionAsync("() => window.scrollY > 0", null, Espera);
        var naFaixa = await pagina.EvaluateAsync<bool[]>(
            "() => { const b = document.querySelector('.topbar').getBoundingClientRect(); const x = Math.round(window.innerWidth / 2);"
            + " return [4, Math.round(b.bottom) + 4].map(y => { const e = document.elementFromPoint(x, y); return !!e && !e.closest('main'); }); }");
        naFaixa.Should().Equal([true, true], "o conteúdo rolado some atrás da faixa da barra superior");
    }

    [SkippableFact]
    public async Task Barra_De_Erro_Do_Blazor_So_Aparece_Quando_O_Framework_A_Exibe()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        var barra = pagina.Locator("#blazor-error-ui");

        // Sem erro: escondida (antes ficava como texto solto no fim de toda página, "Ocorreu um erro inesperado").
        await pagina.EvaluateAsync("() => window.scrollTo(0, document.body.scrollHeight)");
        (await barra.CountAsync()).Should().Be(1);
        (await barra.IsVisibleAsync()).Should().BeFalse("sem exceção no circuito a barra de erro não aparece");

        // Com erro, o Blazor faz style.display = 'block': a barra aparece FIXA no rodapé da janela.
        await pagina.EvaluateAsync("() => { document.getElementById('blazor-error-ui').style.display = 'block'; }");
        (await barra.IsVisibleAsync()).Should().BeTrue();
        (await pagina.EvaluateAsync<string>("() => getComputedStyle(document.getElementById('blazor-error-ui')).position")).Should().Be("fixed");
    }

    private static async Task NavegarPeloMenuAsync(IPage pagina)
    {
        var antes = pagina.Url;
        // Os grupos do menu são dropdowns: sem nenhum item à vista para clicar, abre o primeiro grupo fechado.
        var visiveis = pagina.Locator(".sidebar a.nav-item:not(.active):visible");
        if (await visiveis.CountAsync() == 0)
        {
            await pagina.Locator(".sidebar .nav-group--expansivel:not(.is-open) [data-ui='alternar-grupo']").First.ClickAsync();
        }

        var item = visiveis.First;
        var destino = await item.GetAttributeAsync("href");
        await item.ClickAsync();
        await pagina.WaitForURLAsync(url => url != antes, new PageWaitForURLOptions { Timeout = 30_000 });
        // A navegação aprimorada muda a URL ANTES de trocar o DOM (e a troca fecha a gaveta): espera a página nova — o
        // item clicado marcado como atual pelo servidor — para o próximo clique não cair no DOM antigo.
        await pagina.Locator($".sidebar a.nav-item.active[href='{destino}']").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await pagina.Locator(Shell).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    private static async Task AguardarMenuUsuarioAsync(IPage pagina, bool visivel)
        => await pagina.Locator("#menu-usuario").WaitForAsync(new LocatorWaitForOptions
        {
            State = visivel ? WaitForSelectorState.Visible : WaitForSelectorState.Hidden,
            Timeout = 15_000,
        });

    private static Task<bool> TemClasseAsync(IPage pagina, string classe)
        => pagina.EvaluateAsync<bool>($"() => document.querySelector('{Shell}').classList.contains('{classe}')");

    private static async Task AguardarClasseAsync(IPage pagina, string classe, bool presente)
        => await pagina.WaitForFunctionAsync(
            $"() => {{ const s = document.querySelector('{Shell}'); return !!s && s.classList.contains('{classe}') === {(presente ? "true" : "false")}; }}",
            null,
            Espera);

    private static Task<double> LarguraMenuAsync(IPage pagina)
        => pagina.EvaluateAsync<double>("() => document.querySelector('.sidebar').getBoundingClientRect().width");

    private static Task<string> TemaEfetivoAsync(IPage pagina)
        => pagina.EvaluateAsync<string>("() => window.portalBase.effectiveTheme()");

    private static Task<string> FundoAsync(IPage pagina)
        => pagina.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).getPropertyValue('--ground').trim()");

    private static async Task IconeDeveCorresponderAsync(IPage pagina, string tema)
    {
        (await pagina.Locator("[data-ui='alternar-tema'] .tema-icone--escuro").IsVisibleAsync()).Should().Be(tema == "dark");
        (await pagina.Locator("[data-ui='alternar-tema'] .tema-icone--claro").IsVisibleAsync()).Should().Be(tema == "light");
    }
}
