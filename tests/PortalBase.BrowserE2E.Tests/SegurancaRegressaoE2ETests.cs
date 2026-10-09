using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Prompt 54 — Fases 9/28/41 (TENANT ISOLATION / ERROR STATES / SECURITY REGRESSION) validadas pelo
/// navegador, na superfície que roda sem banco: acesso direto por URL a páginas protegidas NÃO
/// devolve dado de tenant (fail-closed), nenhuma página vaza detalhe interno/segredo, os cabeçalhos
/// de segurança estão presentes e o /health público é mínimo. A prova positiva de isolamento A↛B com
/// dados semeados fica BLOCKED (exige MySQL) — ver FluxosAutenticadosBloqueadosE2ETests.
/// </summary>
[Collection(ColecaoE2E.Nome)]
public sealed class SegurancaRegressaoE2ETests
{
    // Um representante real por módulo (rotas sem parâmetro). Placeholders/param são cobertos à parte.
    private static readonly string[] RotasProtegidas =
    [
        "/",                        // Dashboard
        "/dados/arquivos",
        "/admin",
        "/admin/usuarios",
        "/admin/tenant",
        "/admin/marca",
        "/operacoes/saude",
        "/operacoes/backups",
        "/auditoria/eventos",
        "/auditoria/incidentes",
    ];

    private readonly AmbienteE2E _ambiente;

    public SegurancaRegressaoE2ETests(AmbienteE2E ambiente) => _ambiente = ambiente;

    [SkippableFact] // §41 — acesso direto por URL sem sessão nunca expõe dado de tenant nem detalhe interno
    public async Task Rotas_Protegidas_Sem_Sessao_Sao_FailClosed_E_Sem_Vazamento()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Rotas_Protegidas_Sem_Sessao_Sao_FailClosed_E_Sem_Vazamento), async pagina =>
        {
            foreach (var rota in RotasProtegidas)
            {
                var resposta = await pagina.GotoAsync(
                    _ambiente.BaseUrl + rota,
                    new PageGotoOptions { WaitUntil = WaitUntilState.Load });

                resposta.Should().NotBeNull($"a rota {rota} deve responder");

                // Fail-closed: sem sessão, uma rota protegida NÃO renderiza o portal — redireciona
                // para a tela de login (nunca expõe o shell nem dado de tenant).
                pagina.Url.Should().Contain("/conta/entrar", $"sem sessão, {rota} deve levar ao login (fail-closed)");
                (await pagina.Locator("form[action='/conta/login']").CountAsync())
                    .Should().BeGreaterThan(0, $"sem sessão, {rota} apresenta o formulário de login, não dados de tenant");

                // Nunca 5xx-com-vazamento: o conteúdo renderizado não pode conter stack/credencial/segredo.
                var html = await pagina.ContentAsync();
                AfirmacoesSeguranca.DeveEstarLivreDeVazamento(html, rota);
            }
        });
    }

    [SkippableFact] // §5/§24 — cabeçalhos de segurança presentes numa resposta HTML real
    public async Task Cabecalhos_De_Seguranca_Presentes_Em_Pagina_Html()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Cabecalhos_De_Seguranca_Presentes_Em_Pagina_Html), async pagina =>
        {
            var resposta = await pagina.GotoAsync(
                _ambiente.BaseUrl + "/conta/entrar",
                new PageGotoOptions { WaitUntil = WaitUntilState.Load });

            var cabecalhos = await resposta!.AllHeadersAsync();
            cabecalhos.Should().ContainKey("x-content-type-options").WhoseValue.Should().Contain("nosniff");
            cabecalhos.Should().ContainKey("x-frame-options").WhoseValue.Should().Contain("DENY");
            cabecalhos.Should().ContainKey("content-security-policy");
            cabecalhos["content-security-policy"].Should().Contain("frame-ancestors 'none'");
            cabecalhos.Should().NotContainKey("server");
        });
    }

    [SkippableFact] // §28/§17 — /health público é mínimo (só status), sem detalhe interno
    public async Task Health_Publico_E_Minimo_Pelo_Navegador()
    {
        await _ambiente.ExecutarNavegadorAsync(nameof(Health_Publico_E_Minimo_Pelo_Navegador), async pagina =>
        {
            await pagina.GotoAsync(_ambiente.BaseUrl + "/health/live", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            var texto = await pagina.InnerTextAsync("body");

            texto.Should().Contain("status");
            AfirmacoesSeguranca.DeveEstarLivreDeVazamento(texto, "health/live");
            texto.Should().NotContainAny("Database", "password", "connectionstring");
        });
    }
}
