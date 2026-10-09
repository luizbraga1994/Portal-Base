using Microsoft.Playwright;

namespace PortalBase.BrowserE2E.Tests.Infra;

/// <summary>Sessão autenticada de navegador (contexto + página) — descartável ao fim do teste.</summary>
internal sealed record SessaoAutenticada(IBrowserContext Contexto, IPage Pagina) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Contexto.DisposeAsync();
}

/// <summary>
/// Prompt 76 — autentica um contribuinte semeado pelo FLUXO REAL do sistema, no Browser: POST /conta/login
/// (usuário/senha + antiforgery) → STEP-UP de MFA em /conta/verificar (código TOTP calculado do segredo
/// semeado) → contribuinte (selecionado pelo servidor quando há um só vínculo; senão em /conta/selecionar). NENHUM bypass: não há login de teste, não há
/// desativação de autenticação, não há claim injetada — a sessão passa pelas MESMAS fronteiras
/// (antiforgery, hash de senha, TOTP/anti-replay, cookie com <c>amr=mfa</c>, claims de tenant) da aplicação real.
/// </summary>
internal static class SessaoBrowserE2E
{
    private static readonly PageWaitForURLOptions EsperaNavegacao = new() { Timeout = 30_000 };

    public static Task<SessaoAutenticada> AutenticarAsync(IBrowser navegador, string baseUrl, ContribuinteE2E contribuinte)
    {
        ArgumentNullException.ThrowIfNull(contribuinte);
        return AutenticarAsync(navegador, baseUrl, contribuinte.Credencial, contribuinte.TenantId);
    }

    /// <summary>Login + MFA + seleção do <paramref name="tenantId"/> para QUALQUER credencial semeada (P62-R1).</summary>
    public static async Task<SessaoAutenticada> AutenticarAsync(IBrowser navegador, string baseUrl, CredencialE2E credencial, Guid tenantId)
    {
        var sessao = await NovaSessaoAsync(navegador, baseUrl).ConfigureAwait(false);
        var pagina = sessao.Pagina;

        // 1) LOGIN real (e-mail + senha + antiforgery renderizado pelo formulário).
        await EntrarComSenhaAsync(pagina, credencial).ConfigureAwait(false);

        // 2) STEP-UP de MFA: o cookie sai SEM o fator; o servidor redireciona para /conta/verificar.
        await pagina.WaitForURLAsync("**/conta/verificar**", EsperaNavegacao).ConfigureAwait(false);
        await InformarCodigoMfaAsync(pagina, CodigoTotpTeste.Gerar(credencial.MfaSegredoBase32, DateTimeOffset.UtcNow)).ConfigureAwait(false);

        // 3) CONTRIBUINTE: com UM vínculo o próprio servidor seleciona (mesmas verificações do POST de seleção);
        //    com vários, a tela pede a escolha e o formulário real reemite o cookie com as claims de tenant.
        await pagina.WaitForURLAsync(url => !url.Contains("/conta/verificar", StringComparison.Ordinal), EsperaNavegacao).ConfigureAwait(false);
        if (pagina.Url.Contains("/conta/selecionar", StringComparison.Ordinal))
        {
            await SelecionarContribuinteAsync(pagina, tenantId).ConfigureAwait(false);
        }

        // 4) Autenticado com tenant: sai da área /conta/ (dashboard/returnUrl).
        await pagina.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), EsperaNavegacao).ConfigureAwait(false);

        return sessao;
    }

    /// <summary>Contexto de navegador LIMPO (sem cookies) apontado para o host integrado.</summary>
    public static async Task<SessaoAutenticada> NovaSessaoAsync(IBrowser navegador, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(navegador);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var contexto = await navegador.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl,
            IgnoreHTTPSErrors = false,
            Locale = "pt-BR",
        }).ConfigureAwait(false);

        var pagina = await contexto.NewPageAsync().ConfigureAwait(false);
        return new SessaoAutenticada(contexto, pagina);
    }

    /// <summary>Etapa 1 (e-mail + senha) pelo formulário real — o servidor decide o próximo passo.</summary>
    public static async Task EntrarComSenhaAsync(IPage pagina, CredencialE2E credencial)
    {
        ArgumentNullException.ThrowIfNull(pagina);
        ArgumentNullException.ThrowIfNull(credencial);
        await pagina.GotoAsync("/conta/entrar", new PageGotoOptions { WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);
        await pagina.FillAsync("input#email", credencial.Email).ConfigureAwait(false);
        await pagina.FillAsync("input#senha", credencial.Senha).ConfigureAwait(false);
        await pagina.ClickAsync("button[type='submit']").ConfigureAwait(false);
    }

    /// <summary>Etapa 2 (código TOTP) no formulário real de /conta/verificar.</summary>
    public static async Task InformarCodigoMfaAsync(IPage pagina, string codigo)
    {
        ArgumentNullException.ThrowIfNull(pagina);
        await pagina.FillAsync("input#codigo", codigo).ConfigureAwait(false);
        await pagina.ClickAsync("button[type='submit']").ConfigureAwait(false);
    }

    /// <summary>Etapa 3 (contribuinte) no formulário real de /conta/selecionar.</summary>
    public static async Task SelecionarContribuinteAsync(IPage pagina, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(pagina);
        await pagina.SelectOptionAsync("select#tenantId", tenantId.ToString()).ConfigureAwait(false);
        await pagina.ClickAsync("button[type='submit']").ConfigureAwait(false);
    }

    /// <summary>Espera padrão (30 s) de navegação para um padrão de URL.</summary>
    public static Task AguardarUrlAsync(IPage pagina, string padrao)
    {
        ArgumentNullException.ThrowIfNull(pagina);
        return pagina.WaitForURLAsync(padrao, EsperaNavegacao);
    }
}
