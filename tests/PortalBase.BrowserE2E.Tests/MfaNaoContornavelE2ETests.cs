using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// SEC-001 (auditoria Lei 02, 08/10/2026) — teste permanente de regressão: quem tem MFA ativo NÃO chega a um
/// contribuinte só com a senha. Antes da correção, depois do login era possível ir direto à seleção do contribuinte e
/// abrir dados fiscais sem digitar o código. Agora a sessão da senha só serve para informar o código: a tela de
/// seleção, o POST de seleção (mesmo com antiforgery válido) e qualquer página levam de volta à verificação, sem dado
/// do contribuinte. Informado o código, o acesso segue normal.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class MfaNaoContornavelE2ETests
{
    private static readonly PageWaitForURLOptions Espera = new() { Timeout = 30_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public MfaNaoContornavelE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    [SkippableFact]
    public async Task Sem_O_Codigo_Mfa_Nao_Ha_Selecao_De_Contribuinte_Nem_Dado_Do_Contribuinte()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(_ambiente.Navegador!, _ambiente.BaseUrl);
        var pagina = sessao.Pagina;

        // Só a senha: o servidor pede o código.
        await SessaoBrowserE2E.EntrarComSenhaAsync(pagina, cenario.A.Credencial);
        await pagina.WaitForURLAsync("**/conta/verificar**", Espera);

        // 1) A tela de seleção do contribuinte não abre: volta à verificação.
        await pagina.GotoAsync("/conta/selecionar", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        pagina.Url.Should().Contain("/conta/verificar", "sem o código MFA a seleção do contribuinte não é servida");
        (await pagina.Locator("select#tenantId").CountAsync()).Should().Be(0);

        // 2) Nem o POST de seleção feito à mão, com um token antiforgery válido (o da própria tela de verificação).
        var token = await pagina.Locator("input[name='__RequestVerificationToken']").First.GetAttributeAsync("value");
        token.Should().NotBeNullOrWhiteSpace();
        var resposta = await pagina.APIRequest.PostAsync("/conta/contribuinte", new APIRequestContextOptions
        {
            Form = pagina.APIRequest.CreateFormData()
                .Append("__RequestVerificationToken", token!)
                .Append("tenantId", cenario.A.TenantId.ToString())
                .Append("returnUrl", "/dados/arquivos"),
            MaxRedirects = 0,
        });
        resposta.Status.Should().Be(302);
        resposta.Headers["location"].Should().Contain("/conta/verificar");

        // 3) Nenhuma página com dado do contribuinte abre.
        await pagina.GotoAsync("/dados/arquivos", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        pagina.Url.Should().Contain("/conta/verificar", "sem o código MFA o servidor manda de volta à verificação");
        (await pagina.ContentAsync()).Should().NotContain(cenario.A.NomeArquivoSemente,
            "sem o código MFA nenhum dado do contribuinte pode ser exibido");

        // 4) Com o código, o acesso segue normal até o dado do contribuinte.
        await SessaoBrowserE2E.InformarCodigoMfaAsync(pagina, CodigoTotpTeste.Gerar(cenario.A.Credencial.MfaSegredoBase32, DateTimeOffset.UtcNow));
        await pagina.WaitForURLAsync(url => !url.Contains("/conta/verificar", StringComparison.Ordinal), Espera);
        if (pagina.Url.Contains("/conta/selecionar", StringComparison.Ordinal))
        {
            await SessaoBrowserE2E.SelecionarContribuinteAsync(pagina, cenario.A.TenantId);
            await pagina.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), Espera);
        }

        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync("/dados/arquivos", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        await pagina.Locator($"tr:has-text('{cenario.A.NomeArquivoSemente}')").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }
}
