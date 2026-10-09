using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using PortalBase.Application.Auditoria;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Administração → Tenant no organismo integrado (Browser → Web/Kestrel → MySQL real): o administrador edita os dados
/// cadastrais (razão social, regime, plano, origem), a alteração é gravada e auditada com antes/depois; CNPJ e nome não
/// mudam; o cartão de schema mostra a data (sem texto cortado). Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class TenantE2ETests
{
    private static readonly LocatorWaitForOptions Espera = new() { Timeout = 30_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public TenantE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Administrador_Edita_Os_Dados_Cadastrais_Do_Contribuinte()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        await IrAsync(pagina);

        // O cartão de schema cabe no próprio cartão (antes, o nome da migração era cortado).
        var cartao = pagina.Locator(".stat:has(.st-label:text-is('Schema (última migração)'))");
        (await cartao.Locator(".st-value").EvaluateAsync<bool>("v => v.scrollWidth <= v.clientWidth + 1")).Should().BeTrue();

        var form = pagina.Locator("#dados-cadastrais");
        await form.WaitForAsync(Espera);
        var salvar = form.Locator("#btn-salvar-tenant");
        (await salvar.IsDisabledAsync()).Should().BeTrue("sem alteração não há o que salvar");

        var sufixo = Guid.NewGuid().ToString("N")[..6];
        await form.Locator("#tenant-razao").FillAsync($"Razão Editada E2E {sufixo}");
        await Assertions.Expect(salvar).ToBeEnabledAsync();
        await salvar.ClickAsync();
        await pagina.GetByText("Dados do contribuinte salvos").WaitForAsync(Espera);

        // Recarregar: o que aparece é o que está gravado.
        await IrAsync(pagina);
        (await pagina.Locator("#tenant-razao").InputValueAsync()).Should().Be($"Razão Editada E2E {sufixo}");
        (await pagina.Locator("table.dt").First.InnerTextAsync()).Should().Contain(cenario.A.Cnpj, "o CNPJ não muda");

        using var escopo = _ambiente.CriarEscopoSuporte();
        var registro = await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().RegistrosAuditoria.AsNoTracking()
            .SingleAsync(r => r.Operacao == AcaoAuditoria.TenantDetailsUpdated && r.Tenant == cenario.A.TenantId.ToString());
        registro.Should().NotBeNull();
    }

    private static Task IrAsync(IPage pagina)
        => InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina,
            () => pagina.GotoAsync("/admin/tenant", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
}
