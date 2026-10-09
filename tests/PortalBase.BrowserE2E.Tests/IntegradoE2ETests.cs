using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 55/76 — E2E INTEGRADO (Browser → Web(Kestrel) → MySQL real → Browser). Requer <c>PORTALBASE_TEST_MYSQL_ADMIN</c>
/// (MySQL/Docker). Sem essa infraestrutura — como no ambiente de build — TUDO aqui é <b>BLOCKED</b> (SkippableFact), jamais
/// simulado. COM a variável presente, falha de montagem do ambiente FALHA o teste (P62-R1 — nunca skip silencioso). Os fluxos
/// autenticados (sessão, MFA, RBAC, isolamento, auditoria, upload) estão em <c>FluxosAutenticadosIntegradoE2ETests</c>.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class IntegradoE2ETests
{
    private readonly AmbienteE2EIntegrado _ambiente;

    public IntegradoE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private void ExigirAmbiente() => _ambiente.Exigir();

    // ---------------------------------------------------------------------------------------------
    // Smoke + segurança no host integrado (já reais no P55).
    // ---------------------------------------------------------------------------------------------

    [SkippableFact] // Fase 40 — SMOKE: Browser → Web(Kestrel) → MySQL real (catálogo migrado) → readiness
    public async Task Smoke_Browser_Web_MySQL_Readiness_Healthy()
    {
        ExigirAmbiente();

        await using var contexto = await _ambiente.Navegador!.NewContextAsync(new BrowserNewContextOptions { Locale = "pt-BR" });
        var pagina = await contexto.NewPageAsync();

        await pagina.GotoAsync(_ambiente.BaseUrl + "/health/ready", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        var corpo = await pagina.InnerTextAsync("body");

        corpo.Should().Contain("Healthy", "a readiness só é saudável se a Web falar com o catálogo MySQL real");
        AfirmacoesSeguranca.DeveEstarLivreDeVazamento(corpo, "integrado/health/ready");
    }

    [SkippableFact] // Fase 30 — rota protegida sem sessão é fail-closed, sem vazamento (no host integrado)
    public async Task Seguranca_No_Host_Integrado_E_FailClosed()
    {
        ExigirAmbiente();

        await using var contexto = await _ambiente.Navegador!.NewContextAsync(new BrowserNewContextOptions { Locale = "pt-BR" });
        var pagina = await contexto.NewPageAsync();

        await pagina.GotoAsync(_ambiente.BaseUrl + "/dados/arquivos", new PageGotoOptions { WaitUntil = WaitUntilState.Load });

        AfirmacoesSeguranca.DeveEstarLivreDeVazamento(await pagina.ContentAsync(), "integrado/dados/arquivos");

        // Contrato vigente (o mesmo de SegurancaRegressaoE2ETests): sem sessão, a rota protegida redireciona ao
        // login e apresenta o FORMULÁRIO do servidor — nunca o shell nem dado de tenant. (O link estático
        // a[href='/conta/entrar'] da versão anterior do layout deixou de existir; a asserção antiga era obsoleta.)
        await pagina.WaitForURLAsync("**/conta/entrar**", new PageWaitForURLOptions { Timeout = 30_000 });
        (await pagina.Locator("form[action='/conta/login']").CountAsync())
            .Should().BeGreaterThan(0, "mesmo com MySQL real, sem sessão a rota protegida leva ao formulário de login");
        (await pagina.ContentAsync()).Should().NotContain("Enviar arquivo", "sem sessão nenhuma tela do tenant é exibida");
    }
}
