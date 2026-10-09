using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Menu lateral em dropdown no organismo integrado (Browser → Web/Kestrel → MySQL real), com login real: os grupos
/// (Fiscal, Dados, Transmissão, Administração, Auditoria, Operações) abrem e fecham; ao escolher uma tela, o grupo dela
/// fica aberto e só o item dela fica destacado — inclusive depois da navegação aprimorada, que reescreve o DOM. Também
/// confere a barra superior no celular (tudo dentro da tela). Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class MenuLateralE2ETests
{
    private static readonly PageWaitForFunctionOptions Espera = new() { Timeout = 15_000 };
    private static readonly PageWaitForURLOptions EsperaUrl = new() { Timeout = 30_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public MenuLateralE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Grupo_Da_Tela_Escolhida_Fica_Aberto_E_So_O_Item_Dela_Fica_Destacado()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;

        // No Dashboard (grupo "Principal", sempre à vista) os dropdowns começam fechados.
        await AguardarAtivoAsync(pagina, "/");
        await AguardarGruposAbertosAsync(pagina);
        (await pagina.Locator(".sidebar a[href='/dados/arquivos']").IsVisibleAsync()).Should().BeFalse();

        // Abrir o grupo à mão.
        var dados = Alternar(pagina, "dados");
        await dados.ClickAsync();
        (await dados.GetAttributeAsync("aria-expanded")).Should().Be("true");
        await AguardarGruposAbertosAsync(pagina, "dados");

        // Escolher a tela: o grupo dela continua aberto e só ela fica destacada (com aria-current).
        await pagina.Locator(".sidebar a[href='/dados/arquivos']").ClickAsync();
        await pagina.WaitForURLAsync("**/dados/arquivos", EsperaUrl);
        await AguardarAtivoAsync(pagina, "/dados/arquivos");
        (await pagina.Locator(".sidebar a.nav-item.active").GetAttributeAsync("aria-current")).Should().Be("page");
        await AguardarGruposAbertosAsync(pagina, "dados");
        (await TemClasseAsync(pagina, "dados", "nav-group--ativo")).Should().BeTrue();

        // Outro grupo: "Usuários" fica destacada SOZINHA (não também "Administração", rota /admin).
        await Alternar(pagina, "administracao").ClickAsync();
        await pagina.Locator(".sidebar a[href='/admin/usuarios']").ClickAsync();
        await pagina.WaitForURLAsync("**/admin/usuarios", EsperaUrl);
        await AguardarAtivoAsync(pagina, "/admin/usuarios");
        // "Dados" foi aberto à mão: a preferência sobrevive à navegação aprimorada.
        await AguardarGruposAbertosAsync(pagina, "administracao", "dados");

        // Fechado à mão, "Dados" fica fechado ao recarregar a página...
        await Alternar(pagina, "dados").ClickAsync();
        (await Alternar(pagina, "dados").GetAttributeAsync("aria-expanded")).Should().Be("false");
        await pagina.GotoAsync("/admin/perfis", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await AguardarAtivoAsync(pagina, "/admin/perfis");
        await AguardarGruposAbertosAsync(pagina, "administracao");

        // ...mas abre sozinho quando a tela atual é dele (com filtro na URL inclusive).
        await pagina.GotoAsync("/dados/arquivos?estado=Rejeitado", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await AguardarAtivoAsync(pagina, "/dados/arquivos");
        await AguardarGruposAbertosAsync(pagina, "administracao", "dados");
        (await Alternar(pagina, "dados").GetAttributeAsync("aria-expanded")).Should().Be("true");
    }

    [SkippableFact]
    public async Task Ao_Entrar_De_Novo_So_O_Grupo_Principal_Fica_Aberto()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;

        // Grupos abertos à mão continuam abertos de uma página para outra...
        await AguardarAtivoAsync(pagina, "/");
        await Alternar(pagina, "administracao").ClickAsync();
        await Alternar(pagina, "dados").ClickAsync();
        await AguardarGruposAbertosAsync(pagina, "administracao", "dados");
        await pagina.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load });
        await AguardarGruposAbertosAsync(pagina, "administracao", "dados");

        // ...até sair e entrar de novo (no mesmo navegador): aí só "01 Principal" aparece aberto.
        await pagina.ClickAsync("button.user-btn");
        await pagina.ClickAsync("#menu-usuario-sair");
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/entrar**");

        await SessaoBrowserE2E.EntrarComSenhaAsync(pagina, cenario.A.Credencial);
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/verificar**");
        // O código da sessão anterior não vale de novo (anti-replay): usa o do próximo passo de 30 s.
        await SessaoBrowserE2E.InformarCodigoMfaAsync(pagina, CodigoTotpTeste.Gerar(cenario.A.MfaSegredoBase32, DateTimeOffset.UtcNow.AddSeconds(30)));
        await pagina.WaitForURLAsync(url => !url.Contains("/conta/verificar", StringComparison.Ordinal), EsperaUrl);
        if (pagina.Url.Contains("/conta/selecionar", StringComparison.Ordinal))
        {
            await SessaoBrowserE2E.SelecionarContribuinteAsync(pagina, cenario.A.TenantId);
        }

        await AguardarAtivoAsync(pagina, "/");
        await AguardarGruposAbertosAsync(pagina);
        (await pagina.Locator(".sidebar a[href='/']").IsVisibleAsync()).Should().BeTrue("o grupo Principal (Dashboard) fica sempre à vista");
        (await pagina.Locator(".sidebar a[href='/dados/arquivos']").IsVisibleAsync()).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Menu_Recolhido_Mostra_O_Icone_De_Todos_Os_Itens()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        await AguardarAtivoAsync(pagina, "/");

        await pagina.Locator("[data-ui='recolher-nav']").ClickAsync();
        await pagina.WaitForFunctionAsync("() => document.querySelector('.app-shell').classList.contains('collapsed')", null, Espera);

        // Sem cabeçalho de grupo para abrir, todos os itens aparecem (só o ícone), mesmo os de grupos fechados.
        var total = await pagina.Locator(".sidebar a.nav-item").CountAsync();
        total.Should().BeGreaterThan(5);
        (await pagina.Locator(".sidebar a.nav-item:visible").CountAsync()).Should().Be(total);
        (await pagina.Locator(".sidebar [data-ui='alternar-grupo']:visible").CountAsync()).Should().Be(0);
    }

    [SkippableFact]
    public async Task Barra_Superior_No_Celular_Cabe_Inteira_Na_Tela()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        await pagina.SetViewportSizeAsync(360, 780);
        await pagina.GotoAsync("/", new PageGotoOptions { WaitUntil = WaitUntilState.Load });

        // Menu, ambiente (nunca escondido), tema, usuário (menu da conta), contribuinte e "Trocar": visíveis e dentro da tela.
        foreach (var seletor in new[] { "[data-ui='alternar-nav']", ".env-badge", "[data-ui='alternar-tema']", "button.user-btn", ".ctx-chip.always", "a.ctx-chip--link" })
        {
            var alvo = pagina.Locator(".topbar " + seletor).First;
            (await alvo.IsVisibleAsync()).Should().BeTrue($"{seletor} aparece no celular");
            var caixa = await alvo.BoundingBoxAsync();
            caixa.Should().NotBeNull();
            caixa!.X.Should().BeGreaterThanOrEqualTo(0, seletor);
            (caixa.X + caixa.Width).Should().BeLessThanOrEqualTo(360, $"{seletor} não pode sair da tela");
        }

        // O menu do usuário aberto (Alterar senha / Sair) também cabe na tela.
        await pagina.Locator(".topbar button.user-btn").ClickAsync();
        var menuUsuario = pagina.Locator("#menu-usuario");
        await menuUsuario.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        var caixaMenu = await menuUsuario.BoundingBoxAsync();
        caixaMenu.Should().NotBeNull();
        caixaMenu!.X.Should().BeGreaterThanOrEqualTo(0, "o menu do usuário não sai pela esquerda");
        (caixaMenu.X + caixaMenu.Width).Should().BeLessThanOrEqualTo(360, "o menu do usuário não sai pela direita");
        await pagina.Keyboard.PressAsync("Escape");
        await menuUsuario.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        (await pagina.Locator(".topbar .env-badge").InnerTextAsync()).Trim().Should().NotBeEmpty("o nome do ambiente continua escrito");
        (await pagina.EvaluateAsync<bool>("() => { const t = document.querySelector('.topbar'); return t.scrollWidth <= t.clientWidth; }"))
            .Should().BeTrue("nada transborda da barra");
        // A gaveta começa logo abaixo da barra flutuante (que no celular tem duas linhas), sem ficar por baixo dela.
        (await pagina.EvaluateAsync<bool>(
            "() => { const d = document.querySelector('.sidebar').getBoundingClientRect().top - document.querySelector('.topbar').getBoundingClientRect().bottom; return d >= -1 && d <= 24; }"))
            .Should().BeTrue();
        (await pagina.Locator(".topbar [data-ui='recolher-nav']").IsVisibleAsync()).Should().BeFalse("com o menu em gaveta, recolher não se aplica");
    }

    private static ILocator Alternar(IPage pagina, string grupo)
        => pagina.Locator($".sidebar [data-grupo='{grupo}'] [data-ui='alternar-grupo']");

    // A navegação aprimorada troca a URL antes do conteúdo: espera o menu do servidor marcar o item (um só) como ativo.
    private static async Task AguardarAtivoAsync(IPage pagina, string href)
        => await pagina.WaitForFunctionAsync(
            "href => { const a = document.querySelectorAll('.sidebar a.nav-item.active'); return a.length === 1 && a[0].getAttribute('href') === href; }",
            href,
            Espera);

    // Exatamente estes grupos abertos (em ordem alfabética), com aria-expanded coerente — o JS reaplica a preferência
    // depois do 'enhancedload', então espera em vez de ler na hora.
    private static async Task AguardarGruposAbertosAsync(IPage pagina, params string[] esperados)
    {
        const string Abertos =
            "() => { const g = Array.from(document.querySelectorAll('.sidebar [data-grupo]'));"
            + " if (!g.every(x => x.classList.contains('is-open') === (x.querySelector('[data-ui=\"alternar-grupo\"]').getAttribute('aria-expanded') === 'true'))) { return null; }"
            + " return g.filter(x => x.classList.contains('is-open')).map(x => x.getAttribute('data-grupo')).sort().join(','); }";
        var esperado = string.Join(',', esperados.Order(StringComparer.Ordinal));
        try
        {
            await pagina.WaitForFunctionAsync($"() => ({Abertos})() === '{esperado}'", null, Espera);
        }
        catch (TimeoutException)
        {
            var atual = await pagina.EvaluateAsync<string?>(Abertos);
            atual.Should().Be(esperado, "estes são os grupos que deveriam estar abertos");
            throw;
        }
    }

    private static Task<bool> TemClasseAsync(IPage pagina, string grupo, string classe)
        => pagina.EvaluateAsync<bool>($"() => document.querySelector(\".sidebar [data-grupo='{grupo}']\").classList.contains('{classe}')");
}
