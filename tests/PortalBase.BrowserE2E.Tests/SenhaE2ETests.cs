using FluentAssertions;
using Microsoft.Playwright;
using MimeKit;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Domain.Catalogo;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Senha pelo navegador, no organismo integrado (Browser → Web/Kestrel → MySQL real), com login + MFA reais:
/// "Esqueci a senha" (link lido do e-mail que a Web gravou na pasta local, código fora da URL, link de uso único) e
/// troca de senha em "Minha conta" (a sessão que trocou continua; a outra sessão é encerrada).
/// Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED (skip explicado).
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class SenhaE2ETests
{
    private const string NovaSenha = "Senha-Nova#E2e2026";
    private static readonly PageWaitForURLOptions Espera = new() { Timeout = 30_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public SenhaE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Esqueci_A_Senha_Pelo_Link_Do_Email_Ate_Entrar_Com_A_Nova()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var credencial = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.A.TenantId, PapelConhecido.Leitor);
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var pagina = sessao.Pagina;

        // Da tela de login, pelo link "Esqueci minha senha"; o e-mail em MAIÚSCULAS ainda é o mesmo usuário.
        await pagina.GotoAsync("/conta/entrar", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await pagina.GetByRole(AriaRole.Link, new() { Name = "Esqueci minha senha" }).ClickAsync();
        await pagina.WaitForURLAsync("**/conta/esqueci-senha", Espera);
        // A navegação aprimorada troca a URL antes do conteúdo, e a tela de login também tem #email: usa o formulário certo.
        var pedido = pagina.Locator("form[action='/conta/solicitar-recuperacao']");
        await pedido.WaitForAsync();
        await pedido.Locator("input#email").FillAsync(credencial.Email.ToUpperInvariant());
        await pedido.Locator("button[type='submit']").ClickAsync();
        await pagina.GetByText("Se o e-mail informado estiver cadastrado").WaitForAsync();

        var link = await LinkDoEmailAsync(credencial.Email);
        await pagina.GotoAsync(_ambiente.BaseUrl + link.PathAndQuery, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        pagina.Url.Should().EndWith("/conta/redefinir-senha", "o código sai da URL assim que o link é aberto");
        (await pagina.ContentAsync()).Should().NotContain(System.Web.HttpUtility.ParseQueryString(link.Query)["token"]!);

        // Confirmação diferente: recusada, e o link continua valendo.
        await pagina.FillAsync("input#novaSenha", NovaSenha);
        await pagina.FillAsync("input#confirmacao", NovaSenha + "x");
        await pagina.ClickAsync("button[type='submit']");
        await pagina.GetByText("A confirmação não é igual à nova senha.").WaitForAsync();

        await pagina.FillAsync("input#novaSenha", NovaSenha);
        await pagina.FillAsync("input#confirmacao", NovaSenha);
        await pagina.ClickAsync("button[type='submit']");
        await pagina.WaitForURLAsync("**/conta/entrar?redefinida=1", Espera);
        await pagina.GetByText("Senha redefinida.").WaitForAsync();

        // O link não serve uma segunda vez.
        await pagina.GotoAsync(_ambiente.BaseUrl + link.PathAndQuery, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await pagina.GetByText("Este link é inválido, expirou ou já foi usado.").WaitForAsync();

        // A senha antiga não entra; a nova entra (com o MFA de sempre).
        await SessaoBrowserE2E.EntrarComSenhaAsync(pagina, credencial);
        await pagina.WaitForURLAsync("**/conta/entrar?erro=1", Espera);
        await using var nova = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, credencial with { Senha = NovaSenha }, cenario.A.TenantId);
        nova.Pagina.Url.Should().NotContain("/conta/");
    }

    [SkippableFact]
    public async Task Troca_De_Senha_Em_Minha_Conta_Mantem_A_Sessao_E_Encerra_A_Outra()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var credencial = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.A.TenantId, PapelConhecido.Leitor);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, credencial, cenario.A.TenantId);
        // Mesmo usuário em outro navegador. O código TOTP já usado nesta janela de 30 s é recusado (anti-replay), então
        // as próximas entradas usam códigos de RECUPERAÇÃO (uso único cada).
        await using var outra = await EntrarComCodigoDeRecuperacaoAsync(credencial, credencial.CodigosRecuperacao[0]);
        var pagina = sessao.Pagina;

        // Pelo menu do usuário na barra superior → "Alterar senha".
        await pagina.ClickAsync("button.user-btn");
        await pagina.ClickAsync("#menu-usuario-senha");
        await pagina.WaitForURLAsync("**/conta/minha-conta#alterar-senha", Espera);
        await pagina.Locator("input#senhaAtual").WaitForAsync(); // a navegação aprimorada troca a URL antes do conteúdo
        (await pagina.ContentAsync()).Should().Contain(credencial.Email);

        // Senha atual errada: recusada, nada muda.
        await pagina.FillAsync("input#senhaAtual", "errada-" + credencial.Senha);
        await pagina.FillAsync("input#novaSenha", NovaSenha);
        await pagina.FillAsync("input#confirmacao", NovaSenha);
        await pagina.ClickAsync("button:has-text('Alterar senha')");
        await pagina.GetByText("A senha atual não confere.").WaitForAsync();

        await pagina.FillAsync("input#senhaAtual", credencial.Senha);
        await pagina.FillAsync("input#novaSenha", NovaSenha);
        await pagina.FillAsync("input#confirmacao", NovaSenha);
        await pagina.ClickAsync("button:has-text('Alterar senha')");
        await pagina.GetByText("Senha alterada.").WaitForAsync();

        // A sessão que trocou continua; a outra (mesmo usuário, outro navegador) é encerrada.
        await pagina.GotoAsync("/", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        pagina.Url.Should().NotContain("/conta/entrar");
        await outra.Pagina.GotoAsync("/", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await outra.Pagina.WaitForURLAsync("**/conta/entrar**", Espera);

        // E a senha nova é a que vale.
        await using var nova = await EntrarComCodigoDeRecuperacaoAsync(credencial with { Senha = NovaSenha }, credencial.CodigosRecuperacao[1]);
        nova.Pagina.Url.Should().NotContain("/conta/");
    }

    private async Task<SessaoAutenticada> EntrarComCodigoDeRecuperacaoAsync(CredencialE2E credencial, string codigoRecuperacao)
    {
        var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        await SessaoBrowserE2E.EntrarComSenhaAsync(sessao.Pagina, credencial);
        await sessao.Pagina.WaitForURLAsync("**/conta/verificar**", Espera);
        await SessaoBrowserE2E.InformarCodigoMfaAsync(sessao.Pagina, codigoRecuperacao);
        await sessao.Pagina.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), Espera);
        return sessao;
    }

    // O e-mail que a Web gravou (Email:Modo=PastaLocal) para este destinatário; devolve o link da mensagem.
    private async Task<Uri> LinkDoEmailAsync(string para)
    {
        for (var tentativa = 0; tentativa < 50; tentativa++)
        {
            if (Directory.Exists(_ambiente.PastaEmails))
            {
                foreach (var arquivo in Directory.GetFiles(_ambiente.PastaEmails, "*.eml"))
                {
                    var mensagem = await MimeMessage.LoadAsync(arquivo);
                    if (mensagem.To.Mailboxes.Any(m => string.Equals(m.Address, para, StringComparison.OrdinalIgnoreCase)))
                    {
                        var texto = mensagem.TextBody ?? string.Empty;
                        var inicio = texto.IndexOf("http://portal.e2e.local/", StringComparison.Ordinal);
                        var fim = texto.IndexOfAny([' ', '\r', '\n'], inicio);
                        return new Uri(texto[inicio..fim]);
                    }
                }
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException("O e-mail de redefinição não foi gravado na pasta local.");
    }
}
