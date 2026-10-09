using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using PortalBase.Application.Auditoria;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Domain.Notificacoes;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Configurações → E-mail (SMTP) no organismo integrado (Browser → Web/Kestrel → MySQL real): o Administrador da
/// plataforma salva a configuração pela tela (passa a valer no lugar da do servidor), o teste de envio mostra o
/// resultado em linguagem de negócio, e "voltar à configuração do servidor" desfaz; o administrador de um contribuinte
/// não vê nem usa a tela. Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class EmailSmtpE2ETests
{
    private static readonly LocatorWaitForOptions Espera = new() { Timeout = 60_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public EmailSmtpE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Administrador_Da_Plataforma_Configura_Testa_E_Restaura_O_Smtp_Pela_Tela()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var plataforma = await SemeadorRoundTripE2E.SemearAdministradorPlataformaAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var pagina = sessao.Pagina;
        await SessaoBrowserE2E.EntrarComSenhaAsync(pagina, plataforma);
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/selecionar**");
        await SessaoBrowserE2E.SelecionarContribuinteAsync(pagina, cenario.A.TenantId);
        await pagina.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });

        (await pagina.Locator(".sidebar a[href='/configuracoes/email']").CountAsync()).Should().Be(1, "o item fica no grupo Configurações");
        try
        {
            await IrAsync(pagina);
            await pagina.Locator("#origem-servidor").WaitForAsync(Espera);

            var form = pagina.Locator("#form-email");
            await form.Locator("#email-habilitado").CheckAsync();
            await form.Locator("#email-servidor").FillAsync("smtp.invalid");
            await form.Locator("#email-porta").FillAsync("2525");
            await form.Locator("#email-seguranca").SelectOptionAsync(ConfiguracaoEmail.SegurancaNenhuma);
            await form.Locator("#email-usuario").FillAsync(string.Empty);
            await form.Locator("#email-url").FillAsync("https://portal.exemplo.com");
            await form.Locator("#email-remetente").FillAsync("nao-responda@exemplo.com");
            await form.Locator("#btn-salvar-email").ClickAsync();
            await pagina.Locator(".toast:has-text('Configuração de e-mail salva')").WaitForAsync(Espera);
            await pagina.Locator(".stat:has-text('Nesta tela')").WaitForAsync(Espera);
            (await pagina.Locator("#origem-servidor").CountAsync()).Should().Be(0);

            // Aparência: a prévia troca de tema na hora; salvar grava (auditado) e a tela volta com o tema escolhido.
            await pagina.Locator("#previa-email:has-text('Acessar o Portal Base')").WaitForAsync(Espera);
            await pagina.Locator("#email-tema").SelectOptionAsync("Azul");
            await AteAsync(async () => (await pagina.Locator("#previa-email").InnerHTMLAsync()).Contains("#0B4F9C", StringComparison.OrdinalIgnoreCase));
            await pagina.Locator("#email-rodape").FillAsync("Empresa <b>teste</b>");
            await AteAsync(async () => (await pagina.Locator("#previa-email").InnerTextAsync()).Contains("Empresa <b>teste</b>", StringComparison.Ordinal));
            (await pagina.Locator("#previa-email b").CountAsync()).Should().Be(0, "o texto do rodapé nunca vira HTML");
            await pagina.Locator("#btn-salvar-aparencia").ClickAsync();
            await pagina.Locator(".toast:has-text('Aparência dos e-mails salva')").WaitForAsync(Espera);
            using (var escopoAparencia = _ambiente.CriarEscopoSuporte())
            {
                var gravada = await escopoAparencia.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().ConfiguracoesEmail
                    .AsNoTracking().SingleAsync(c => c.Id == ConfiguracaoEmail.IdUnico);
                gravada.Tema.Should().Be("Azul");
                gravada.TextoRodape.Should().Be("Empresa <b>teste</b>", "guardado como texto; só vira HTML codificado no e-mail");
            }

            (await pagina.Locator("#email-tema").InputValueAsync()).Should().Be("Azul");

            // Teste de envio: servidor inexistente → mensagem de negócio, na hora.
            await pagina.Locator("#email-teste-para").FillAsync("maria@exemplo.com");
            await pagina.Locator("#btn-testar-email").ClickAsync();
            await pagina.Locator("#resultado-teste-email:has-text('Não foi possível conectar ao servidor de e-mail')").WaitForAsync(Espera);

            // Voltar à configuração do servidor.
            await pagina.Locator("#btn-restaurar-email").ClickAsync();
            await pagina.Locator(".modal-foot button.btn-primary").ClickAsync();
            await pagina.Locator("#origem-servidor").WaitForAsync(Espera);
        }
        finally
        {
            using var escopo = _ambiente.CriarEscopoSuporte();
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            await catalogo.ConfiguracoesEmail.Where(c => c.Id == ConfiguracaoEmail.IdUnico).ExecuteDeleteAsync();
        }

        using var verificacao = _ambiente.CriarEscopoSuporte();
        (await verificacao.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().RegistrosAuditoria.AsNoTracking()
            .CountAsync(r => r.Operacao == AcaoAuditoria.EmailConfigurationUpdated || r.Operacao == AcaoAuditoria.EmailTestSent))
            .Should().BeGreaterThanOrEqualTo(4, "salvar, aparência, testar e restaurar ficam na auditoria");
    }

    private static async Task AteAsync(Func<Task<bool>> condicao)
    {
        for (var i = 0; i < 60; i++)
        {
            if (await condicao())
            {
                return;
            }

            await Task.Delay(250);
        }

        (await condicao()).Should().BeTrue("a tela deveria refletir a mudança em até 15 s");
    }

    [SkippableFact]
    public async Task Administrador_Do_Contribuinte_Nao_Ve_Nem_Usa_A_Tela_De_Email()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;

        (await pagina.Locator(".sidebar a[href='/configuracoes/email']").CountAsync()).Should().Be(0);
        (await pagina.Locator(".sidebar a[href='/admin/marca']").CountAsync()).Should().Be(1, "as configurações do contribuinte continuam no grupo Configurações");
        await IrAsync(pagina);
        (await pagina.Locator("main").InnerTextAsync()).Should().Contain("Sem permissão");
        (await pagina.Locator("#form-email").CountAsync()).Should().Be(0);
    }

    private static Task IrAsync(IPage pagina)
        => InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina,
            () => pagina.GotoAsync("/configuracoes/email", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
}
