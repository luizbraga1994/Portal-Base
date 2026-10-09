using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Administração → Contribuintes (plataforma) no organismo integrado (Browser → Web/Kestrel → MySQL real): o
/// Administrador da plataforma cadastra um contribuinte novo pela tela — o portal cria e prepara a base de dados e o
/// deixa Ativo; CNPJ repetido é recusado; o administrador de um contribuinte não vê o item nem usa a tela.
/// Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class ContribuintesPlataformaE2ETests
{
    private static readonly LocatorWaitForOptions Espera = new() { Timeout = 60_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public ContribuintesPlataformaE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Administrador_Da_Plataforma_Cadastra_Contribuinte_Que_Fica_Ativo_Com_Base_Propria()
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

        // O item "Contribuintes" existe no menu da plataforma.
        (await pagina.Locator(".sidebar a[href='/admin/contribuintes']").CountAsync()).Should().Be(1);
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina,
            () => pagina.GotoAsync("/admin/contribuintes", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));

        var nome = "E2E" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var cnpj = CnpjValido();
        var form = pagina.Locator("#novo-contribuinte");
        await form.Locator("#contrib-cnpj").FillAsync($"{cnpj[..2]}.{cnpj[2..5]}.{cnpj[5..8]}/{cnpj[8..12]}-{cnpj[12..]}");
        await form.Locator("#contrib-nome").FillAsync(nome.ToLowerInvariant());
        await form.Locator("#contrib-razao").FillAsync($"Empresa {nome} Ltda");
        await form.Locator("#btn-criar-contribuinte").ClickAsync();
        await pagina.Locator("#resultado-contribuinte:has-text('Contribuinte criado')").WaitForAsync(Espera);
        await pagina.Locator($"#tabela-contribuintes tr[data-contribuinte='{nome}']:has-text('Ativo')").WaitForAsync(Espera);

        using (var escopo = _ambiente.CriarEscopoSuporte())
        {
            var tenant = await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().Tenants.AsNoTracking()
                .SingleAsync(t => t.Nome == nome);
            tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
            tenant.NomeBaseFisica.Should().Be("portalbase_tenant_" + nome.ToLowerInvariant());
            tenant.SchemaVersion.Should().NotBeNullOrWhiteSpace("a base recebeu a estrutura (migrations)");
        }

        // O mesmo CNPJ de novo: recusado antes de tocar em qualquer base.
        await form.Locator("#contrib-cnpj").FillAsync(cnpj);
        await form.Locator("#contrib-nome").FillAsync(nome + "X");
        await form.Locator("#contrib-razao").FillAsync("Outra");
        await form.Locator("#btn-criar-contribuinte").ClickAsync();
        await pagina.Locator("#resultado-contribuinte:has-text('Já existe um contribuinte com este CNPJ')").WaitForAsync(Espera);
    }

    [SkippableFact]
    public async Task Administrador_Do_Contribuinte_Nao_Ve_Nem_Usa_A_Tela()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;

        (await pagina.Locator(".sidebar a[href='/admin/contribuintes']").CountAsync()).Should().Be(0);
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina,
            () => pagina.GotoAsync("/admin/contribuintes", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        (await pagina.Locator("main").InnerTextAsync()).Should().Contain("Sem permissão");
        (await pagina.Locator("#novo-contribuinte").CountAsync()).Should().Be(0);
    }

    [SkippableFact]
    public async Task Administrador_Da_Plataforma_Abre_Contribuintes_E_Email_Sem_Escolher_Contribuinte()
    {
        // Instalação nova: não há contribuinte para escolher. A seleção oferece "Cadastrar contribuinte" e as telas de
        // plataforma abrem sem contribuinte selecionado (só elas — o resto continua pedindo a seleção).
        _ambiente.Exigir();
        await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var plataforma = await SemeadorRoundTripE2E.SemearAdministradorPlataformaAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var pagina = sessao.Pagina;
        await SessaoBrowserE2E.EntrarComSenhaAsync(pagina, plataforma);
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/selecionar**");

        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.Locator("#link-cadastrar-contribuinte").ClickAsync());
        await pagina.WaitForURLAsync("**/admin/contribuintes", new PageWaitForURLOptions { Timeout = 30_000 });
        await pagina.Locator("#novo-contribuinte").WaitForAsync(Espera);
        (await pagina.Locator("#link-escolher-contribuinte").CountAsync()).Should().Be(1, "sem contribuinte, a barra oferece escolher um");
        (await pagina.Locator(".sidebar a[href='/admin/contribuintes']").CountAsync()).Should().Be(1);
        (await pagina.Locator(".sidebar a[href='/configuracoes/email']").CountAsync()).Should().Be(1);
        (await pagina.Locator(".sidebar a[href='/']").CountAsync()).Should().Be(0, "o menu sem contribuinte mostra só as telas de plataforma");
        (await pagina.Locator(".sidebar a[href='/admin/usuarios']").CountAsync()).Should().Be(0);

        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina,
            () => pagina.GotoAsync("/configuracoes/email", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        await pagina.Locator("#form-email").WaitForAsync(Espera);

        // As telas de contribuinte continuam exigindo a escolha.
        await pagina.GotoAsync("/admin/usuarios", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await pagina.WaitForURLAsync("**/conta/selecionar**", new PageWaitForURLOptions { Timeout = 30_000 });
    }

    [SkippableFact]
    public async Task Sem_Contribuinte_Escolhido_Quem_Nao_E_Da_Plataforma_Volta_A_Selecao()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var admin = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.A.TenantId, PapelConhecido.Administrador);
        await SemeadorRoundTripE2E.VincularAsync(_ambiente, admin.UsuarioId, cenario.B.TenantId, PapelConhecido.Administrador);
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var pagina = sessao.Pagina;
        await SessaoBrowserE2E.EntrarComSenhaAsync(pagina, admin);
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/verificar**");
        await SessaoBrowserE2E.InformarCodigoMfaAsync(pagina, CodigoTotpTeste.Gerar(admin.MfaSegredoBase32, DateTimeOffset.UtcNow));
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/selecionar**");
        (await pagina.Locator("#link-cadastrar-contribuinte").CountAsync()).Should().Be(0, "o atalho é só do Administrador da plataforma");

        foreach (var rota in new[] { "/admin/contribuintes", "/configuracoes/email" })
        {
            await pagina.GotoAsync(rota, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await pagina.WaitForURLAsync("**/conta/selecionar**", new PageWaitForURLOptions { Timeout = 30_000 });
        }
    }

    private static string CnpjValido()
    {
        var d = Guid.NewGuid().ToByteArray().Take(12).Select(b => b % 10).ToList();
        d.Add(Dv(d, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));
        d.Add(Dv(d, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));
        return string.Concat(d);

        static int Dv(List<int> digitos, int[] pesos)
        {
            var resto = pesos.Select((p, i) => p * digitos[i]).Sum() % 11;
            return resto < 2 ? 0 : 11 - resto;
        }
    }
}
