using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MimeKit;
using PortalBase.Application.Multitenancy;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// MFA e senha temporária no cadastro de usuários, pelo navegador, no organismo integrado (Browser → Web/Kestrel → MySQL
/// real), com login real: o administrador cadastra um usuário com o MFA EXIGIDO e ele, no primeiro acesso, só consegue
/// cadastrar o autenticador (QR code, chave, códigos de recuperação) antes de usar o portal; o administrador exige,
/// remove o MFA (com o próprio step-up) e gera uma senha temporária que chega por e-mail e obriga a troca no próximo
/// acesso. Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED (skip explicado).
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class MfaUsuariosE2ETests
{
    private static readonly PageWaitForURLOptions Espera = new() { Timeout = 30_000 };
    private static readonly LocatorWaitForOptions EsperaLocal = new() { Timeout = 30_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public MfaUsuariosE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Usuario_Com_Mfa_Exigido_Cadastra_O_Autenticador_Pelo_Qr_Code_Antes_De_Usar_O_Portal()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var login = $"mfa.{sufixo}";
        var credencial = new CredencialE2E(Guid.Empty, login, "Senha-Inicial#2026x", string.Empty);

        // O administrador cadastra o usuário com "Exigir MFA" marcado.
        await using (var admin = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A))
        {
            var pagina = admin.Pagina;
            await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync("/admin/usuarios/novo", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
            var form = pagina.Locator("#novo-usuario");
            await form.Locator("#novo-nome").FillAsync("Usuário MFA E2E");
            await form.Locator("#novo-email").FillAsync(credencial.Email);
            await form.Locator("#novo-login").FillAsync(login);
            await form.Locator("input[type='checkbox'][value='Leitor']").CheckAsync();
            await form.Locator("#senha-inicial-radio").CheckAsync();
            await form.Locator("#novo-senha").FillAsync(credencial.Senha);
            await form.Locator("#novo-exigir-mfa").CheckAsync();
            await form.Locator("#btn-cadastrar").ClickAsync();
            await pagina.WaitForURLAsync(url => url.Contains("/admin/usuarios/", StringComparison.Ordinal) && !url.EndsWith("/novo", StringComparison.Ordinal), Espera);
            await pagina.Locator("#mfa-usuario:has-text('exigida')").WaitForAsync(EsperaLocal);
        }

        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var p = sessao.Pagina;

        // Login → direto ao cadastro do MFA; nenhuma outra página é servida antes (decisão do servidor).
        await SessaoBrowserE2E.EntrarComSenhaAsync(p, credencial);
        await p.WaitForURLAsync("**/conta/cadastrar-mfa", Espera);
        foreach (var outra in new[] { "/", "/admin/usuarios", "/conta/selecionar", "/conta/minha-conta" })
        {
            await p.GotoAsync(outra, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            p.Url.Should().EndWith("/conta/cadastrar-mfa", $"'{outra}' não abre com o cadastro do MFA pendente");
        }

        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(p, () => p.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load }));
        await p.Locator("#mfa-obrigatorio").WaitForAsync(EsperaLocal);
        await p.Locator("#btn-gerar-qr").ClickAsync();

        // QR code gerado no servidor (PNG em data: URI) + a chave para digitar.
        var qr = p.Locator("#qr-mfa");
        await qr.WaitForAsync(EsperaLocal);
        (await qr.GetAttributeAsync("src")).Should().StartWith("data:image/png;base64,");
        (await qr.EvaluateAsync<int>("img => img.naturalWidth")).Should().BeGreaterThan(100, "a imagem do QR code carrega (CSP img-src data:)");
        var segredo = (await p.Locator("#chave-mfa").InnerTextAsync()).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        segredo.Should().MatchRegex("^[A-Z2-7]{16,}$");

        // Código errado: recusado, continua na mesma etapa.
        await p.Locator("#codigo-mfa").FillAsync("000000");
        await p.Locator("#btn-confirmar-mfa").ClickAsync();
        await p.Locator(".alert-bad:has-text('Código inválido')").WaitForAsync(EsperaLocal);

        await p.Locator("#codigo-mfa").FillAsync(CodigoTotpTeste.Gerar(segredo, DateTimeOffset.UtcNow));
        await p.Locator("#btn-confirmar-mfa").ClickAsync();
        await p.Locator("#mfa-cadastrado").WaitForAsync(EsperaLocal);
        var codigos = await p.Locator("#codigos-recuperacao li").AllInnerTextsAsync();
        codigos.Should().HaveCount(8).And.OnlyHaveUniqueItems();
        (await p.Locator("#chave-mfa").CountAsync()).Should().Be(0, "o segredo sai da tela depois do cadastro");

        // "Entrar de novo": sai e volta ao login com o aviso.
        await p.Locator("#btn-concluir-mfa").ClickAsync();
        await p.WaitForURLAsync("**/conta/entrar?mfa=cadastrado", Espera);
        await p.Locator("#aviso-mfa-cadastrado").WaitForAsync(EsperaLocal);

        // Agora o login pede o segundo fator (aqui, um código de recuperação) e o portal abre.
        await SessaoBrowserE2E.EntrarComSenhaAsync(p, credencial);
        await p.WaitForURLAsync("**/conta/verificar**", Espera);
        await SessaoBrowserE2E.InformarCodigoMfaAsync(p, codigos[0]);
        await p.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), Espera);

        var usuario = await UsuarioPorEmailAsync(credencial.Email);
        usuario.MfaHabilitado.Should().BeTrue();
        usuario.MfaObrigatorio.Should().BeTrue();
    }

    [SkippableFact]
    public async Task Administrador_Exige_Remove_O_Mfa_E_Envia_Senha_Temporaria_Que_Obriga_A_Troca()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var alvo = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.A.TenantId, PapelConhecido.Leitor);

        await using (var admin = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A))
        {
            var pagina = admin.Pagina;
            await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync($"/admin/usuarios/{alvo.UsuarioId}", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
            var cartao = pagina.Locator("#mfa-usuario");
            await cartao.Locator("text=cadastrada").First.WaitForAsync(EsperaLocal);

            // Exigir e deixar de exigir.
            await cartao.Locator("#btn-exigir-mfa:has-text('Exigir MFA')").ClickAsync();
            await pagina.Locator(".toast:has-text('MFA exigido')").WaitForAsync(EsperaLocal);
            (await UsuarioAsync(alvo.UsuarioId)).MfaObrigatorio.Should().BeTrue();
            await cartao.Locator("#btn-exigir-mfa:has-text('Deixar de exigir')").ClickAsync();
            await pagina.Locator(".toast:has-text('deixou de ser exigido')").WaitForAsync(EsperaLocal);
            (await UsuarioAsync(alvo.UsuarioId)).MfaObrigatorio.Should().BeFalse();

            // Remover o MFA (o administrador está com o step-up feito no login): confirmação → apagado.
            await cartao.Locator("#btn-remover-mfa").ClickAsync();
            await pagina.Locator(".modal-foot button.btn-danger").ClickAsync();
            await pagina.Locator(".toast:has-text('MFA removido')").WaitForAsync(EsperaLocal);
            await cartao.Locator("text=não cadastrada").WaitForAsync(EsperaLocal);
            (await cartao.Locator("#btn-remover-mfa").CountAsync()).Should().Be(0);
            var semMfa = await UsuarioAsync(alvo.UsuarioId);
            semMfa.MfaHabilitado.Should().BeFalse();
            semMfa.SegredoMfaProtegido.Should().BeNull();

            // Senha temporária: confirmação → e-mail.
            await pagina.Locator("#btn-senha-temporaria").ClickAsync();
            await pagina.Locator(".modal-foot button.btn-danger").ClickAsync();
            await pagina.Locator(".toast:has-text('Senha temporária enviada')").WaitForAsync(EsperaLocal);
            await cartao.Locator(".badge-warn:has-text('senha temporária')").WaitForAsync(EsperaLocal);
            (await pagina.Locator("main").InnerTextAsync()).Should().NotContain(alvo.Senha);

            // Vários contribuintes: o administrador de A também administra B; na seção "Contribuintes" do cadastro ele dá ao
            // usuário acesso a B (mesma conta, perfis do sistema) e depois revoga — sem cadastrar a pessoa de novo.
            // (por último: com acesso — mesmo revogado — a outro contribuinte, os dados da conta passam a ser do
            // Administrador da plataforma, e os botões de MFA/senha acima ficariam travados para este administrador).
            await SemeadorRoundTripE2E.VincularAsync(_ambiente, cenario.A.UsuarioId, cenario.B.TenantId, PapelConhecido.Administrador);
            await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load }));
            var contribuintes = pagina.Locator("#contribuintes-usuario");
            var acessoB = contribuintes.Locator($"#usuario-contribuinte-{cenario.B.TenantId:N}");
            await acessoB.WaitForAsync(EsperaLocal);
            (await contribuintes.Locator($"#usuario-contribuinte-{cenario.A.TenantId:N}").IsDisabledAsync()).Should().BeTrue("o contribuinte da sessão tem os próprios controles");
            (await contribuintes.Locator("input[type='checkbox'][value='Leitor']").IsCheckedAsync()).Should().BeTrue("começa com os perfis do sistema que ele tem aqui");
            await acessoB.CheckAsync();
            await contribuintes.Locator("#btn-salvar-contribuintes").ClickAsync();
            await pagina.Locator(".toast:has-text('acesso concedido')").WaitForAsync(EsperaLocal);
            (await VinculoAsync(alvo.UsuarioId, cenario.B.TenantId))!.Status.Should().Be(StatusAcesso.Ativo);
            await contribuintes.Locator("tr[data-contribuinte] td:has-text('Leitor')").First.WaitForAsync(EsperaLocal);

            await acessoB.UncheckAsync();
            await contribuintes.Locator("#btn-salvar-contribuintes").ClickAsync();
            await pagina.Locator(".toast:has-text('acesso revogado')").WaitForAsync(EsperaLocal);
            (await VinculoAsync(alvo.UsuarioId, cenario.B.TenantId))!.Status.Should().Be(StatusAcesso.Revogado);
        }

        var temporaria = await SenhaTemporariaDoEmailAsync(alvo.Email);
        temporaria.Should().NotBe(alvo.Senha);

        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var p = sessao.Pagina;

        // A senha antiga não vale mais.
        await SessaoBrowserE2E.EntrarComSenhaAsync(p, alvo);
        await p.Locator(".alert-bad").WaitForAsync(EsperaLocal);
        p.Url.Should().Contain("/conta/entrar");

        // Com a temporária (sem MFA agora): o portal leva à troca obrigatória da senha, e nada mais abre antes.
        var comTemporaria = alvo with { Senha = temporaria };
        await SessaoBrowserE2E.EntrarComSenhaAsync(p, comTemporaria);
        await p.WaitForURLAsync("**/conta/minha-conta?obrigatoria=1", Espera);
        await p.Locator("#aviso-senha-temporaria").WaitForAsync(EsperaLocal);
        await p.GotoAsync("/admin/usuarios", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        p.Url.Should().Contain("/conta/minha-conta?obrigatoria=1");

        const string Definitiva = "Senha-Definitiva#2026y";
        await p.Locator("input#senhaAtual").WaitForAsync(EsperaLocal);
        await p.FillAsync("input#senhaAtual", temporaria);
        await p.FillAsync("input#novaSenha", Definitiva);
        await p.FillAsync("input#confirmacao", Definitiva);
        await p.ClickAsync("button:has-text('Alterar senha')");
        await p.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), Espera);
        await p.GotoAsync("/admin/usuarios", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        p.Url.Should().NotContain("/conta/minha-conta", "resolvida a pendência, o portal volta a abrir");

        (await UsuarioAsync(alvo.UsuarioId)).TrocarSenhaNoProximoAcesso.Should().BeFalse();

        // Com sessão válida, "sair" sem o token antiforgery continua recusado (anti-CSRF) e a sessão segue.
        var semToken = await p.APIRequest.PostAsync("/conta/logout", new APIRequestContextOptions { MaxRedirects = 0, Form = p.APIRequest.CreateFormData() });
        semToken.Status.Should().Be(400);
        await p.GotoAsync("/admin/usuarios", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        p.Url.Should().NotContain("/conta/entrar");
    }

    // O e-mail que a Web gravou (Email:Modo=PastaLocal) com a senha temporária: a linha recuada do texto.
    private async Task<string> SenhaTemporariaDoEmailAsync(string para)
    {
        for (var tentativa = 0; tentativa < 50; tentativa++)
        {
            if (Directory.Exists(_ambiente.PastaEmails))
            {
                foreach (var arquivo in Directory.GetFiles(_ambiente.PastaEmails, "*.eml"))
                {
                    var mensagem = await MimeMessage.LoadAsync(arquivo);
                    if ((mensagem.Subject ?? string.Empty).Contains("senha temporária", StringComparison.Ordinal)
                        && mensagem.To.Mailboxes.Any(m => string.Equals(m.Address, para, StringComparison.OrdinalIgnoreCase)))
                    {
                        return (mensagem.TextBody ?? string.Empty).Split('\n')
                            .Single(l => l.StartsWith("    ", StringComparison.Ordinal)).Trim();
                    }
                }
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException("O e-mail com a senha temporária não foi gravado na pasta local.");
    }

    private async Task<UsuarioTenant?> VinculoAsync(Guid usuario, Guid tenant)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        return await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().UsuarioTenants.AsNoTracking()
            .SingleOrDefaultAsync(v => v.UsuarioId == usuario && v.TenantId == tenant);
    }

    private async Task<Usuario> UsuarioAsync(Guid id)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        return await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().Usuarios.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    private async Task<Usuario> UsuarioPorEmailAsync(string email)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        return await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().Usuarios.AsNoTracking().SingleAsync(u => u.Email == email);
    }
}
