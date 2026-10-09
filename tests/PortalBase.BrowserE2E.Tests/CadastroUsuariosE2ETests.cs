using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using PortalBase.Application.Multitenancy;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Infrastructure.Multitenancy;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// Cadastro de usuários e perfis pelo navegador, no organismo integrado (Browser → Web/Kestrel → MySQL real), com login
/// real: o administrador cadastra um usuário (que entra de verdade), altera e exclui; cria um perfil personalizado; e o
/// "Administrador da plataforma" — sem vínculo e sem MFA — escolhe um contribuinte qualquer e executa uma operação de
/// alto impacto. Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED (skip explicado).
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class CadastroUsuariosE2ETests
{
    private static readonly PageWaitForURLOptions Espera = new() { Timeout = 30_000 };
    private static readonly LocatorWaitForOptions EsperaLocal = new() { Timeout = 30_000 };

    private readonly AmbienteE2EIntegrado _ambiente;

    public CadastroUsuariosE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    [SkippableFact]
    public async Task Administrador_Cadastra_Altera_E_Exclui_Usuario_Pela_Tela()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var email = $"novo.{sufixo}@exemplo.com";
        const string Senha = "Senha-Inicial#2026x";

        // Lista → "+ Novo usuário".
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync("/admin/usuarios", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        await pagina.Locator("#btn-novo-usuario").ClickAsync();
        await pagina.WaitForURLAsync("**/admin/usuarios/novo", Espera);
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load }));

        var form = pagina.Locator("#novo-usuario");
        await form.Locator("#novo-nome").FillAsync("Novo Usuário E2E");
        await form.Locator("#novo-email").FillAsync(email);
        await form.Locator("#novo-login").FillAsync($"novo.{sufixo}");
        await form.Locator("input[type='checkbox'][value='Leitor']").CheckAsync();
        await form.Locator("#senha-inicial-radio").CheckAsync();
        await form.Locator("#novo-senha").FillAsync(Senha);
        await form.Locator("#btn-cadastrar").ClickAsync();

        // Vai para o cadastro dele, com o perfil marcado.
        await pagina.WaitForURLAsync(url => url.Contains("/admin/usuarios/", StringComparison.Ordinal) && !url.EndsWith("/novo", StringComparison.Ordinal), Espera);
        await pagina.Locator("#perfis-usuario input[type='checkbox'][value='Leitor']:checked").WaitForAsync(EsperaLocal);
        var usuarioId = Guid.Parse(pagina.Url.Split('/').Last());
        (await UsuarioAsync(usuarioId))!.Email.Should().Be(email);

        // Ele entra de verdade (sem MFA, um só contribuinte → direto no portal).
        await using (var dele = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl))
        {
            await SessaoBrowserE2E.EntrarComSenhaAsync(dele.Pagina, new CredencialE2E(usuarioId, $"novo.{sufixo}", Senha, string.Empty));
            await dele.Pagina.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), Espera);
        }

        // Alterar o nome.
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load }));
        await pagina.Locator("#editar-nome").FillAsync("Nome Alterado E2E");
        await pagina.Locator("#btn-salvar-dados").ClickAsync();
        await pagina.Locator(".toast:has-text('Dados salvos')").WaitForAsync(EsperaLocal);
        (await UsuarioAsync(usuarioId))!.Nome.Should().Be("Nome Alterado E2E");

        // Excluir do contribuinte (único) → conta inativada e volta à lista.
        await pagina.Locator("#btn-excluir").ClickAsync();
        await pagina.Locator(".modal-foot button.btn-danger").ClickAsync();
        await pagina.WaitForURLAsync(url => url.EndsWith("/admin/usuarios", StringComparison.Ordinal), Espera);
        var excluido = await UsuarioAsync(usuarioId);
        excluido!.Status.Should().Be(StatusUsuario.Inativo);
        (await VinculosAsync(usuarioId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Administrador_Cria_Perfil_Personalizado_Com_As_Permissoes_Marcadas()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        var nome = $"Auditor {Guid.NewGuid().ToString("N")[..6]}";

        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync("/admin/perfis/novo", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        await pagina.Locator("#perfil-nome").FillAsync(nome);
        await pagina.Locator("#perm-dados-visualizar").CheckAsync();
        await pagina.Locator("#perm-auditoria-visualizar").CheckAsync();
        await pagina.Locator("#btn-salvar-perfil").ClickAsync();

        await pagina.WaitForURLAsync(url => url.EndsWith("/admin/perfis", StringComparison.Ordinal), Espera);
        await pagina.Locator($"[data-perfil='{nome}']").WaitForAsync(EsperaLocal);

        using var escopo = _ambiente.CriarEscopoSuporte();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var perfil = await catalogo.Papeis.AsNoTracking().SingleAsync(p => p.Nome == nome);
        perfil.TenantId.Should().Be(cenario.A.TenantId, "o perfil é do contribuinte em que foi criado");
        var chaves = await (
            from pp in catalogo.PapelPermissoes.AsNoTracking()
            join p in catalogo.Permissoes.AsNoTracking() on pp.PermissaoId equals p.Id
            where pp.PapelId == perfil.Id
            select p.Chave).ToListAsync();
        chaves.Should().BeEquivalentTo(["dados.visualizar", "auditoria.visualizar"]);
    }

    [SkippableFact]
    public async Task Administrador_Da_Plataforma_Entra_Sem_Mfa_Escolhe_Qualquer_Contribuinte_E_Administra()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var plataforma = await SemeadorRoundTripE2E.SemearAdministradorPlataformaAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var pagina = sessao.Pagina;

        // Senha só (sem MFA cadastrado) → escolha do contribuinte, que lista contribuintes SEM vínculo dele.
        await SessaoBrowserE2E.EntrarComSenhaAsync(pagina, plataforma);
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/selecionar**");
        await SessaoBrowserE2E.SelecionarContribuinteAsync(pagina, cenario.A.TenantId);
        await pagina.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), Espera);

        // Dado do contribuinte (sem vínculo dele) e a administração do contribuinte abrem — decidido no servidor.
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync("/dados/arquivos", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        await pagina.Locator($"tr:has-text('{cenario.A.NomeArquivoSemente}')").First.WaitForAsync(EsperaLocal);
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync("/admin/tenant", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        await pagina.GetByText(cenario.A.RazaoSocial).First.WaitForAsync(EsperaLocal);

        // A plataforma inteira está liberada (permissões de plataforma) — ex.: incidentes.
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync("/auditoria/incidentes", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
        (await pagina.Locator("main").InnerTextAsync()).Should().NotContain("Sem permissão");
    }

    [SkippableFact]
    public async Task Perfil_Administrador_Da_Plataforma_Aparece_Na_Lista_E_So_Outro_Admin_Da_Plataforma_Concede()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var alvo = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.A.TenantId, PapelConhecido.Leitor);

        // Administrador do CONTRIBUINTE: o perfil global aparece listado, sem botão, com o motivo.
        await using (var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A))
        {
            var pagina = sessao.Pagina;
            await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync($"/admin/usuarios/{alvo.UsuarioId}", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
            var cartao = pagina.Locator("#perfis-usuario #perfil-plataforma");
            await cartao.WaitForAsync(EsperaLocal);
            (await cartao.InnerTextAsync()).Should().Contain("Administrador da plataforma").And.Contain("Não concedido");
            (await pagina.Locator("#btn-conceder-plataforma").CountAsync()).Should().Be(0, "só um Administrador da plataforma concede");
            (await pagina.Locator("#motivo-plataforma").InnerTextAsync()).Should().Contain("Só um Administrador da plataforma");

            // No próprio cadastro: explica como se obtém o primeiro.
            await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.GotoAsync($"/admin/usuarios/{cenario.A.UsuarioId}", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));
            await pagina.Locator("#perfis-usuario #perfil-plataforma").WaitForAsync(EsperaLocal);
            (await pagina.Locator("#motivo-plataforma").InnerTextAsync()).Should().Contain("criar-acesso-local.ps1");
            // Nada do cartão (texto longo de configuração, etiqueta "global") sai pela borda.
            (await pagina.EvaluateAsync<bool>(
                "() => { const c = document.querySelector('#perfil-plataforma').getBoundingClientRect(); return [...document.querySelectorAll('#perfil-plataforma *')].every(e => { const r = e.getBoundingClientRect(); return r.width === 0 || (r.left >= c.left - 1 && r.right <= c.right + 1); }); }"))
                .Should().BeTrue("o conteúdo do cartão Administrador da plataforma cabe nele");
        }

        // Administrador da PLATAFORMA: concede e remove pelo cartão da lista de perfis (com confirmação), gravado no catálogo.
        var plataforma = await SemeadorRoundTripE2E.SemearAdministradorPlataformaAsync(_ambiente);
        await using var dele = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var p = dele.Pagina;
        await SessaoBrowserE2E.EntrarComSenhaAsync(p, plataforma);
        await SessaoBrowserE2E.AguardarUrlAsync(p, "**/conta/selecionar**");
        await SessaoBrowserE2E.SelecionarContribuinteAsync(p, cenario.A.TenantId);
        await p.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), Espera);
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(p, () => p.GotoAsync($"/admin/usuarios/{alvo.UsuarioId}", new PageGotoOptions { WaitUntil = WaitUntilState.Load }));

        await p.Locator("#perfil-plataforma #btn-conceder-plataforma").ClickAsync();
        await p.Locator(".modal-foot button.btn-danger").ClickAsync();
        await p.Locator(".toast:has-text('Administrador da plataforma concedido')").WaitForAsync(EsperaLocal);
        await p.Locator("#perfil-plataforma[data-concedido='sim']").WaitForAsync(EsperaLocal);
        (await UsuarioAsync(alvo.UsuarioId))!.AdministradorPlataforma.Should().BeTrue();

        await p.Locator("#perfil-plataforma #btn-remover-plataforma").ClickAsync();
        await p.Locator(".modal-foot button.btn-danger").ClickAsync();
        await p.Locator("#perfil-plataforma[data-concedido='nao']").WaitForAsync(EsperaLocal);
        (await UsuarioAsync(alvo.UsuarioId))!.AdministradorPlataforma.Should().BeFalse();
    }

    private async Task<Usuario?> UsuarioAsync(Guid id)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        return await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    }

    private async Task<int> VinculosAsync(Guid usuarioId)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        return await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().UsuarioTenants.AsNoTracking().CountAsync(v => v.UsuarioId == usuarioId);
    }
}
