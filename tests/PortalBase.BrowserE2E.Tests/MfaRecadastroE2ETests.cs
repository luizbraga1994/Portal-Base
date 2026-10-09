using FluentAssertions;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Recadastro do MFA (outro celular) no organismo integrado: iniciado e ABANDONADO, a conta continua protegida pelo
/// autenticador ATUAL (o novo segredo fica só pendente); concluído, o autenticador novo passa a valer e o antigo não
/// entra mais. Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class MfaRecadastroE2ETests
{
    private static readonly LocatorWaitForOptions Espera = new() { Timeout = 60_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public MfaRecadastroE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Recadastro_Abandonado_Nao_Tira_O_Mfa_E_Concluido_Troca_O_Autenticador()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var credencial = cenario.A.Credencial;

        // Sessão 1: inicia o recadastro e gera o NOVO segredo (sem confirmar ainda).
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var p = sessao.Pagina;
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(p,
            () => p.GotoAsync("/conta/cadastrar-mfa?recadastrar=1", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        await p.Locator("#btn-gerar-qr").ClickAsync();
        await p.Locator("#chave-mfa").WaitForAsync(Espera);
        var novo = (await p.Locator("#chave-mfa").InnerTextAsync()).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        novo.Should().NotBe(credencial.MfaSegredoBase32);

        // Abandonado: outra sessão ainda entra com o autenticador ANTIGO (código do próximo passo — sem reuso).
        var inicio = DateTimeOffset.UtcNow;
        (await EntrarAsync(credencial, CodigoTotpTeste.Gerar(credencial.MfaSegredoBase32, inicio.AddSeconds(30))))
            .Should().BeTrue("o recadastro pendente não desliga o segundo fator atual");

        // Concluído na sessão 1 com o NOVO autenticador.
        await p.Locator("#codigo-mfa").FillAsync(CodigoTotpTeste.Gerar(novo, DateTimeOffset.UtcNow));
        await p.Locator("#btn-confirmar-mfa").ClickAsync();
        await p.Locator("#mfa-cadastrado").WaitForAsync(Espera);

        // Agora o antigo não entra mais; o novo entra. Mesmo passo de tempo para os dois, AINDA NÃO CONSUMIDO (o
        // anti-replay recusa qualquer passo já usado, então a recusa do antigo só pode ser pela troca do segredo) e
        // DENTRO da tolerância de ±1 passo: espera dois passos depois do login antigo e usa o passo seguinte ao atual.
        var passoInicial = inicio.ToUnixTimeSeconds() / 30;
        while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 < passoInicial + 2)
        {
            await Task.Delay(500);
        }

        var passo = DateTimeOffset.UtcNow.AddSeconds(30);
        (await EntrarAsync(credencial, CodigoTotpTeste.Gerar(credencial.MfaSegredoBase32, passo)))
            .Should().BeFalse("o autenticador antigo foi substituído");
        (await EntrarAsync(credencial, CodigoTotpTeste.Gerar(novo, passo)))
            .Should().BeTrue();
    }

    // Login com senha + código; devolve se o segundo fator foi aceito (saiu de /conta/verificar).
    private async Task<bool> EntrarAsync(CredencialE2E credencial, string codigo)
    {
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var p = sessao.Pagina;
        await SessaoBrowserE2E.EntrarComSenhaAsync(p, credencial);
        await p.WaitForURLAsync("**/conta/verificar**", new PageWaitForURLOptions { Timeout = 30_000 });
        await SessaoBrowserE2E.InformarCodigoMfaAsync(p, codigo);
        await p.WaitForLoadStateAsync(LoadState.Load);
        try
        {
            await p.WaitForURLAsync(url => !url.Contains("/conta/verificar", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 10_000 });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
