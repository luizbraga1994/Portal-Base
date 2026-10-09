using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Multitenancy;
using PortalBase.BrowserE2E.Tests.Infra;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Marca;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.BrowserE2E.Tests;

/// <summary>
/// P62-R1 — FLUXOS AUTENTICADOS REAIS no organismo integrado (Browser → Web/Kestrel → MySQL real; Worker real
/// hospedado). Substitui os 11 placeholders do Prompt 54 (<c>FluxosAutenticadosBloqueadosE2ETests</c> e os dois
/// de <c>AutenticacaoE2ETests</c>), que LANÇAVAM por desenho quando havia MySQL. Cada cenário usa login + senha +
/// TOTP reais, seleção de contribuinte pelo servidor e as MESMAS fronteiras da produção (antiforgery, cookie
/// protegido, RBAC/tenant/MFA no servidor). Nada é simulado; nenhum requisito é inventado — cada asserção
/// verifica um comportamento já definido no código do sistema (citado no comentário do teste).
/// Sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>: BLOCKED (skip explicado). Com a variável: falha real se o ambiente não subir.
/// </summary>
[Collection(ColecaoE2EIntegrado.Nome)]
public sealed class FluxosAutenticadosIntegradoE2ETests
{
    private const string CookieSessao = "portalbase.auth";
    private const int MaxTentativas = 20;
    private const int IntervaloMs = 1000;

    // PNG 1×1 válido (assinatura + IHDR + IDAT + IEND) — arquivo de teste seguro, gerado aqui (nenhum binário versionado).
    private static readonly byte[] PngValido = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly AmbienteE2EIntegrado _ambiente;

    public FluxosAutenticadosIntegradoE2ETests(AmbienteE2EIntegrado ambiente) => _ambiente = ambiente;

    private IBrowser Navegador => _ambiente.Navegador!;

    // ---------------------------------------------------------------------------------------------
    // Autenticação / sessão (ex-AutenticacaoE2ETests.Login_Valido_Sessao_E_Logout / Sessao_Expirada_Redireciona)
    // ---------------------------------------------------------------------------------------------

    [SkippableFact] // EndpointsAutenticacao: /conta/login → /conta/mfa → /conta/contribuinte → sessão; /conta/logout encerra
    public async Task Login_Valido_Sessao_E_Logout()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;

        // Sessão válida: a rota protegida renderiza dado do tenant (não redireciona ao login).
        await IrAsync(pagina, "/dados/arquivos");
        await AguardarLinhaAsync(pagina, cenario.A.NomeArquivoSemente);
        pagina.Url.Should().NotContain("/conta/entrar");
        (await sessao.Contexto.CookiesAsync()).Should().Contain(
            c => c.Name.StartsWith(CookieSessao, StringComparison.Ordinal) && c.HttpOnly,
            "a sessão é um cookie HttpOnly emitido pelo servidor");

        // LOGOUT real (formulário com antiforgery em /conta/sair → POST /conta/logout).
        await IrAsync(pagina, "/conta/sair");
        await pagina.ClickAsync("form[action='/conta/logout'] button[type='submit']");
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/entrar**");
        (await sessao.Contexto.CookiesAsync()).Should().NotContain(
            c => c.Name.StartsWith(CookieSessao, StringComparison.Ordinal), "o logout remove o cookie de sessão");

        // Após o logout a mesma rota volta a ser fail-closed.
        await IrAsync(pagina, "/dados/arquivos");
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/entrar**");
        (await pagina.ContentAsync()).Should().NotContain(cenario.A.NomeArquivoSemente);
    }

    [SkippableFact] // ConfiguracaoSeguranca: cookie com ExpireTimeSpan (8 h) — ticket expirado é rejeitado pelo servidor
    public async Task Sessao_Expirada_Redireciona_Ao_Login()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);

        // Reemite o MESMO ticket da sessão, protegido com a chave REAL do servidor, porém já vencido (emitido há
        // 8 h 01 min) — exatamente o que o navegador apresentaria após a expiração, sem esperar 8 horas.
        var formato = FormatoTicket();
        var ticket = formato.Unprotect(await LerCookieSessaoAsync(sessao.Contexto))
            ?? throw new InvalidOperationException("O cookie de sessão emitido pelo servidor não pôde ser lido.");
        var agora = DateTimeOffset.UtcNow;
        ticket.Properties.IssuedUtc = agora.AddHours(-8).AddMinutes(-1);
        ticket.Properties.ExpiresUtc = agora.AddMinutes(-1);
        await SubstituirCookieSessaoAsync(sessao.Contexto, formato.Protect(ticket));

        await IrAsync(sessao.Pagina, "/dados/arquivos");
        await SessaoBrowserE2E.AguardarUrlAsync(sessao.Pagina, "**/conta/entrar**");
        (await sessao.Pagina.ContentAsync()).Should().NotContain(cenario.A.NomeArquivoSemente,
            "sessão expirada não pode exibir dado do tenant");
        AfirmacoesSeguranca.DeveEstarLivreDeVazamento(await sessao.Pagina.ContentAsync(), "integrado/sessao-expirada");
    }

    [SkippableFact] // Cookie protegido por Data Protection: valor adulterado no cliente não autentica
    public async Task Sessao_Adulterada_No_Cliente_E_Rejeitada()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);

        var valor = await LerCookieSessaoAsync(sessao.Contexto);
        var meio = valor.Length / 2;
        var adulterado = string.Concat(valor.AsSpan(0, meio), valor[meio] == 'A' ? "B" : "A", valor.AsSpan(meio + 1));
        await SubstituirCookieSessaoAsync(sessao.Contexto, adulterado);

        await IrAsync(sessao.Pagina, "/dados/arquivos");
        await SessaoBrowserE2E.AguardarUrlAsync(sessao.Pagina, "**/conta/entrar**");
        (await sessao.Pagina.ContentAsync()).Should().NotContain(cenario.A.NomeArquivoSemente);
    }

    // ---------------------------------------------------------------------------------------------
    // MFA (ex-placeholder Mfa_Valido_E_Invalido — válido / inválido / obrigatório)
    // ---------------------------------------------------------------------------------------------

    [SkippableFact] // EndpointsAutenticacao.ValidarMfaAsync: código inválido → /conta/verificar?erro=1; válido → seleção
    public async Task Mfa_Invalido_Bloqueia_E_Valido_Libera()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var pagina = sessao.Pagina;

        await SessaoBrowserE2E.EntrarComSenhaAsync(pagina, cenario.A.Credencial);
        await SessaoBrowserE2E.AguardarUrlAsync(pagina, "**/conta/verificar**");

        await SessaoBrowserE2E.InformarCodigoMfaAsync(pagina, CodigoInvalido(cenario.A.MfaSegredoBase32));
        await pagina.WaitForURLAsync(
            url => url.Contains("/conta/verificar", StringComparison.Ordinal) && url.Contains("erro=1", StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 30_000 });
        (await pagina.Locator("[role='alert']").CountAsync()).Should().BeGreaterThan(0, "o erro do 2º fator é informado ao usuário");
        AfirmacoesSeguranca.DeveEstarLivreDeVazamento(await pagina.ContentAsync(), "integrado/mfa-invalido");

        await SessaoBrowserE2E.InformarCodigoMfaAsync(pagina, CodigoTotpTeste.Gerar(cenario.A.MfaSegredoBase32, DateTimeOffset.UtcNow));
        // Válido libera: um único vínculo, então o servidor já entra no contribuinte (sai da área /conta/).
        await pagina.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });
    }

    [SkippableFact] // EndpointsAutenticacao.ConcluirComContribuinteAsync: 1 vínculo → selecionado pelo servidor; 2 → tela de escolha
    public async Task Contribuinte_Unico_Pula_A_Selecao_E_Varios_Exigem_Escolha()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var usuario = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.A.TenantId, PapelConhecido.Leitor);

        // UM vínculo: login + MFA levam direto ao portal, já no contribuinte A (claim emitida pelo servidor).
        await using (var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl))
        {
            await SessaoBrowserE2E.EntrarComSenhaAsync(sessao.Pagina, usuario);
            await SessaoBrowserE2E.AguardarUrlAsync(sessao.Pagina, "**/conta/verificar**");
            await SessaoBrowserE2E.InformarCodigoMfaAsync(sessao.Pagina, CodigoTotpTeste.Gerar(usuario.MfaSegredoBase32, DateTimeOffset.UtcNow));
            await sessao.Pagina.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });
            var contribuinte = sessao.Pagina.Locator(".ctx-chip.always .v");
            await contribuinte.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            // O contribuinte é identificado pelo NOME escolhido do tenant, sempre em maiúsculas.
            (await contribuinte.InnerTextAsync()).Should().Be(cenario.A.Nome.ToUpperInvariant());
        }

        // DOIS vínculos: o servidor NÃO escolhe — a tela oferece exatamente os dois contribuintes do usuário.
        await SemeadorRoundTripE2E.VincularAsync(_ambiente, usuario.UsuarioId, cenario.B.TenantId, PapelConhecido.Leitor);
        await using (var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl))
        {
            await SessaoBrowserE2E.EntrarComSenhaAsync(sessao.Pagina, usuario);
            await SessaoBrowserE2E.AguardarUrlAsync(sessao.Pagina, "**/conta/verificar**");
            // Código de recuperação: o TOTP do passo atual já foi consumido acima (anti-replay).
            await SessaoBrowserE2E.InformarCodigoMfaAsync(sessao.Pagina, usuario.CodigosRecuperacao[0]);
            await SessaoBrowserE2E.AguardarUrlAsync(sessao.Pagina, "**/conta/selecionar**");
            var opcoes = await sessao.Pagina.Locator("select#tenantId option[value]:not([value=''])").EvaluateAllAsync<string[]>("os => os.map(o => o.value)");
            opcoes.Should().BeEquivalentTo([cenario.A.TenantId.ToString(), cenario.B.TenantId.ToString()]);
            // O combobox mostra o NOME do tenant (MAIÚSCULO), não a razão social nem o CNPJ.
            var textos = await sessao.Pagina.Locator("select#tenantId option[value]:not([value=''])").EvaluateAllAsync<string[]>("os => os.map(o => o.textContent.trim())");
            textos.Should().BeEquivalentTo([cenario.A.Nome.ToUpperInvariant(), cenario.B.Nome.ToUpperInvariant()]);
        }

        // UM vínculo, mas contribuinte INDISPONÍVEL (desabilitado): fail-closed — nada é selecionado, vai para a tela.
        var soDeB = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.B.TenantId, PapelConhecido.Leitor);
        using (var escopo = _ambiente.CriarEscopoSuporte())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var tenantB = await catalogo.Tenants.SingleAsync(t => t.Id == cenario.B.TenantId);
            tenantB.Desabilitar(DateTimeOffset.UtcNow);
            await catalogo.SaveChangesAsync();
        }

        await using (var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl))
        {
            await SessaoBrowserE2E.EntrarComSenhaAsync(sessao.Pagina, soDeB);
            await SessaoBrowserE2E.AguardarUrlAsync(sessao.Pagina, "**/conta/verificar**");
            await SessaoBrowserE2E.InformarCodigoMfaAsync(sessao.Pagina, CodigoTotpTeste.Gerar(soDeB.MfaSegredoBase32, DateTimeOffset.UtcNow));
            await SessaoBrowserE2E.AguardarUrlAsync(sessao.Pagina, "**/conta/selecionar**");
            (await sessao.Pagina.Locator($"select#tenantId option[value='{cenario.B.TenantId}']").IsDisabledAsync())
                .Should().BeTrue("o contribuinte desabilitado aparece, mas não pode ser escolhido");
            (await sessao.Contexto.CookiesAsync()).Should().Contain(c => c.Name.StartsWith(CookieSessao, StringComparison.Ordinal));
            await IrAsync(sessao.Pagina, "/dados/arquivos");
            (await sessao.Pagina.ContentAsync()).Should().NotContain(cenario.B.NomeArquivoSemente.ToString(), "sem contribuinte selecionado, nenhum dado de B");
        }
    }

    [SkippableFact] // Mfa.razor ("ou um código de recuperação") + GestaoMfa.ValidarAsync: recuperação aceita, uso único
    public async Task Codigo_De_Recuperacao_E_Aceito_Pela_Tela_E_Vale_Uma_Unica_Vez()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var usuario = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.A.TenantId, PapelConhecido.Leitor);
        var codigo = usuario.CodigosRecuperacao[0];
        codigo.Should().MatchRegex("[A-Z0-9-]{9,}", "é o formato exibido ao usuário na ativação (letras, dígitos e hífen)");

        // 1º uso: o PRÓPRIO formulário do navegador (validação HTML incluída) aceita e o servidor libera o acesso
        // (um único vínculo: entra direto no contribuinte, saindo da área /conta/).
        await using (var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl))
        {
            await SessaoBrowserE2E.EntrarComSenhaAsync(sessao.Pagina, usuario);
            await SessaoBrowserE2E.AguardarUrlAsync(sessao.Pagina, "**/conta/verificar**");
            await SessaoBrowserE2E.InformarCodigoMfaAsync(sessao.Pagina, codigo);
            await sessao.Pagina.WaitForURLAsync(url => !url.Contains("/conta/", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });
        }

        // 2º uso do MESMO código: recusado pelo servidor (uso único).
        await using (var sessao = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl))
        {
            await SessaoBrowserE2E.EntrarComSenhaAsync(sessao.Pagina, usuario);
            await SessaoBrowserE2E.AguardarUrlAsync(sessao.Pagina, "**/conta/verificar**");
            await SessaoBrowserE2E.InformarCodigoMfaAsync(sessao.Pagina, codigo);
            await sessao.Pagina.WaitForURLAsync(
                url => url.Contains("/conta/verificar", StringComparison.Ordinal) && url.Contains("erro=1", StringComparison.Ordinal),
                new PageWaitForURLOptions { Timeout = 30_000 });
        }
    }

    // ---------------------------------------------------------------------------------------------
    // RBAC (ex-placeholder Rbac_Permitido_E_Negado)
    // ---------------------------------------------------------------------------------------------

    [SkippableFact] // CatalogoPapeisPermissoes: Leitor = só dados.visualizar (sem admin.tenant / auditoria.visualizar)
    public async Task Rbac_Leitor_Negado_Na_Ui_E_No_Servidor_Administrador_Permitido()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var leitor = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.A.TenantId, PapelConhecido.Leitor);

        await using (var sessaoLeitor = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, leitor, cenario.A.TenantId))
        {
            var pagina = sessaoLeitor.Pagina;

            // UI: administração do contribuinte (admin.tenant) e trilha de auditoria (auditoria.visualizar) negadas.
            await IrAsync(pagina, "/admin/tenant");
            await pagina.GetByText("Sem permissão").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await pagina.Locator("main").InnerTextAsync()).Should().NotContain(cenario.A.Cnpj);

            await IrAsync(pagina, "/auditoria/eventos");
            await pagina.GetByText("Sem permissão").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            // SERVIDOR: a UI não oferece o upload ao Leitor; um POST FORJADO (antiforgery válido do próprio usuário)
            // é negado pelo serviço (admin.tenant) — prova de que a autorização não depende da tela.
            var destino = await PostarLogoContribuinteAsync(pagina, PngValido, "logo.png");
            destino.Should().Contain("/admin/marca?erro=4", "a autorização (admin.tenant) é decidida no servidor");
        }

        (await ContarAuditoriaAsync(cenario.A.TenantId, AcaoAuditoria.MarcaTenantAtualizada)).Should().Be(0,
            "o POST forjado do Leitor não alterou a marca do contribuinte");

        // Controle positivo: o Administrador do mesmo contribuinte acessa a administração do contribuinte.
        await using var sessaoAdmin = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        await IrAsync(sessaoAdmin.Pagina, "/admin/tenant");
        await sessaoAdmin.Pagina.GetByText(cenario.A.RazaoSocial).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    // ---------------------------------------------------------------------------------------------
    // Isolamento A↛B (ex-placeholder Tenant_Isolation_A_Nao_Acessa_B)
    // ---------------------------------------------------------------------------------------------

    [SkippableFact] // EndpointsAutenticacao.SelecionarContribuinteAsync (só vínculos ATIVOS) + read-models por tenant
    public async Task Tenant_Isolation_A_Nao_Acessa_B_Por_Selecao_Forjada_Nem_Por_Id()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;

        // 1) FORM forjado: injeta a opção do tenant B no select de A e envia → recusado (erro=1), cookie mantém A.
        await IrAsync(pagina, "/conta/selecionar");
        await pagina.EvaluateAsync(
            @"id => { const s = document.querySelector('select#tenantId'); const o = document.createElement('option');
                      o.value = id; o.text = 'forjado'; s.appendChild(o); s.value = id; }",
            cenario.B.TenantId.ToString());
        await pagina.ClickAsync("button[type='submit']");
        await pagina.WaitForURLAsync(
            url => url.Contains("/conta/selecionar", StringComparison.Ordinal) && url.Contains("erro=1", StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 30_000 });

        await IrAsync(pagina, "/dados/arquivos");
        await AguardarLinhaAsync(pagina, cenario.A.NomeArquivoSemente);
        (await pagina.Locator($"tr:has-text('{cenario.B.NomeArquivoSemente}')").CountAsync()).Should().Be(0,
            "a seleção forjada de B não troca o contexto: os arquivos continuam sendo os de A");

        // 2) IDOR por ID na URL: o usuário de B não é visível na administração do tenant A.
        await IrAsync(pagina, $"/admin/usuarios/{cenario.B.UsuarioId}");
        await pagina.Locator("main").WaitForAsync();
        await Task.Delay(IntervaloMs); // circuito interativo concluir o carregamento
        (await pagina.Locator("main").InnerTextAsync()).Should().NotContain(cenario.B.Login,
            "o detalhe por ID não expõe usuário de outro contribuinte");

        // Controle positivo do mesmo recurso no próprio tenant.
        await IrAsync(pagina, $"/admin/usuarios/{cenario.A.UsuarioId}");
        await pagina.GetByText(cenario.A.Login).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    // ---------------------------------------------------------------------------------------------
    // Command real pela UI (ex-placeholder Command_Executa_Pela_Ui)
    // ---------------------------------------------------------------------------------------------

    [SkippableFact] // UsuarioDetalhe → IDadosGestaoUsuarios.DefinirPerfisAsync → GestaoUsuarios (admin.usuarios + anti-escalada + auditoria)
    public async Task Command_Definir_Perfis_Pela_Ui_Persiste_No_MySQL_E_Reflete_Na_Tela()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var leitor = await SemeadorRoundTripE2E.SemearUsuarioAsync(_ambiente, cenario.A.TenantId, PapelConhecido.Leitor);
        (await PapeisDoVinculoAsync(leitor.UsuarioId, cenario.A.TenantId)).Should().NotContain(nameof(PapelConhecido.Analista));

        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => IrAsync(pagina, $"/admin/usuarios/{leitor.UsuarioId}"));
        await pagina.GetByText(leitor.Login).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // Marca "Analista" (além de "Leitor") e salva — com o circuito já interativo, o servidor recebe cada clique.
        var analista = pagina.Locator("#perfis-usuario input[type='checkbox'][value='Analista']");
        await analista.CheckAsync();
        await pagina.Locator("#btn-salvar-perfis").ClickAsync();
        await pagina.Locator(".toast:has-text('Perfis salvos')").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // Persistência REAL no catálogo MySQL + reflexo na tela (read-model) após recarregar.
        (await AguardarAsync(async () => (await PapeisDoVinculoAsync(leitor.UsuarioId, cenario.A.TenantId)).Contains(nameof(PapelConhecido.Analista))))
            .Should().BeTrue("o command real persiste o novo perfil no vínculo do usuário");
        (await PapeisDoVinculoAsync(leitor.UsuarioId, cenario.A.TenantId)).Should().Contain(nameof(PapelConhecido.Leitor), "os outros perfis marcados continuam");
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => IrAsync(pagina, $"/admin/usuarios/{leitor.UsuarioId}"));
        (await pagina.Locator("#perfis-usuario input[type='checkbox'][value='Analista']").IsCheckedAsync()).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // Duplo envio (ex-placeholder Duplo_Envio_Gera_Uma_Operacao)
    // ---------------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------------
    // Auditoria de ação crítica vista pelo read-model autorizado (ex-placeholder Acao_Critica_Gera_Auditoria)
    // ---------------------------------------------------------------------------------------------

    [SkippableFact] // Browser → command (logo do contribuinte) → IAuditoria → ConsultaAuditoriaAutorizada → /auditoria/eventos
    public async Task Acao_Critica_Gera_Auditoria_Visivel_No_ReadModel_Autorizado()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;

        // Ação administrativa REAL pela tela: troca da logo do contribuinte (admin.tenant + auditoria).
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => IrAsync(pagina, "/admin/marca"));
        await EnviarLogoPelaTelaAsync(pagina, "form[action='/admin/marca/contribuinte'] input[type='file']",
            "form[action='/admin/marca/contribuinte'] button[type='submit']", new FilePayload { Name = "logo.png", MimeType = "image/png", Buffer = PngValido });
        (await AguardarAsync(async () => await ContarAuditoriaAsync(cenario.A.TenantId, AcaoAuditoria.MarcaTenantAtualizada) == 1))
            .Should().BeTrue();

        // A trilha é lida pela TELA (read-model autorizado por auditoria.visualizar, filtrado no servidor pelo tenant).
        await IrAsync(pagina, "/auditoria/eventos");
        await AguardarRodapeAsync(pagina, " de "); // a trilha real do tenant carregou
        await InteracaoBlazorE2E.RepetirAteAsync(
            async () =>
            {
                await pagina.FillAsync("input[aria-label='Ação']", AcaoAuditoria.MarcaTenantAtualizada);
                await pagina.ClickAsync("button:has-text('Filtrar')");
            },
            pagina.Locator(".dt-foot:has-text('1–1 de 1')"));
        var registro = pagina.Locator($"table.dt tbody tr:has-text('{AcaoAuditoria.MarcaTenantAtualizada}')");
        (await registro.CountAsync()).Should().Be(1);
        (await registro.First.InnerTextAsync()).Should().Contain("Permitido");
        // O ator aparece pelo NOME e login do usuário (catálogo), não pelo identificador interno.
        var ator = registro.First.Locator("td").Nth(1);
        (await ator.InnerTextAsync()).Should().Contain("Usuário E2E").And.Contain(cenario.A.Login)
            .And.NotContain(cenario.A.UsuarioId.ToString(), "o identificador fica só na dica (title), para conferência");
        (await ator.GetAttributeAsync("title")).Should().Contain(cenario.A.UsuarioId.ToString());
        var html = await pagina.ContentAsync();
        AfirmacoesSeguranca.DeveEstarLivreDeVazamento(html, "integrado/auditoria/marca");
        html.Should().NotContain(cenario.A.Senha).And.NotContain(cenario.A.MfaSegredoBase32);
    }

    // ---------------------------------------------------------------------------------------------
    // Paginação / filtros / ordenação server-side (ex-placeholder Paginacao_Filtros_Ordenacao_Com_Dados)
    // ---------------------------------------------------------------------------------------------

    [SkippableFact] // ConsultaAuditoriaEfCore: WHERE tenant + filtros exatos; ORDER BY OcorridoEmUtc DESC; Skip/Take (25/página)
    public async Task Paginacao_Filtros_Ordenacao_Da_Auditoria_Com_Dados_Reais()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        var ator = "e2e-pag-" + Guid.NewGuid().ToString("N")[..8];
        var inicio = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(8), TimeSpan.Zero);

        // 30 eventos reais em A (10 negados + 20 permitidos, 1 por minuto) e 7 do MESMO ator em B (isolamento).
        var eventosA = Enumerable.Range(0, 30).Select(i => i % 3 == 0
            ? (AcaoAuditoria.LoginFailed, ResultadoAuditoria.Negado, inicio.AddMinutes(i))
            : (AcaoAuditoria.AuditQueried, ResultadoAuditoria.Permitido, inicio.AddMinutes(i)));
        await SemeadorRoundTripE2E.SemearEventosAuditoriaAsync(_ambiente, cenario.A.TenantId, ator, eventosA);
        await SemeadorRoundTripE2E.SemearEventosAuditoriaAsync(_ambiente, cenario.B.TenantId, ator,
            Enumerable.Range(0, 7).Select(i => (AcaoAuditoria.AuditQueried, ResultadoAuditoria.Permitido, inicio.AddMinutes(100 + i))));

        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        await IrAsync(pagina, "/auditoria/eventos");
        await pagina.Locator("input[aria-label='Ator']").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // Página 1: 25 de 30 (os 7 de B nunca entram), mais recente primeiro.
        await InteracaoBlazorE2E.RepetirAteAsync(
            async () =>
            {
                await pagina.FillAsync("input[aria-label='Ator']", ator);
                await pagina.ClickAsync("button:has-text('Filtrar')");
            },
            pagina.Locator(".dt-foot:has-text('1–25 de 30')"));
        (await pagina.Locator(".dt-foot").InnerTextAsync()).Should().Contain("página 1 de 2");
        var pagina1 = await DatasDasLinhasAsync(pagina);
        pagina1.Should().HaveCount(25);
        pagina1[0].Should().Be(Formatar(inicio.AddMinutes(29)), "ordenação server-side: mais recente primeiro");
        pagina1.Should().BeInDescendingOrder(StringComparer.Ordinal);

        // Página 2: os 5 restantes, terminando no mais antigo.
        await InteracaoBlazorE2E.RepetirAteAsync(
            () => pagina.ClickAsync("button[aria-label='Próxima página']"),
            pagina.Locator(".dt-foot:has-text('26–30 de 30')"));
        var pagina2 = await DatasDasLinhasAsync(pagina);
        pagina2.Should().HaveCount(5);
        pagina2[^1].Should().Be(Formatar(inicio));
        string.CompareOrdinal(pagina1[^1], pagina2[0]).Should().BeGreaterThan(0, "a página 2 continua a ordem da página 1");

        // Filtro por RESULTADO (servidor): só os 10 negados.
        await pagina.SelectOptionAsync("select[aria-label='Resultado']", "Negado");
        await pagina.ClickAsync("button:has-text('Filtrar')");
        await AguardarRodapeAsync(pagina, "1–10 de 10");
        (await pagina.Locator("table.dt tbody tr").AllInnerTextsAsync()).Should().OnlyContain(t => t.Contains("Negado"));

        // Filtro por AÇÃO (servidor): os 20 AuditQueried do ator.
        await pagina.SelectOptionAsync("select[aria-label='Resultado']", "Todos");
        await pagina.FillAsync("input[aria-label='Ação']", AcaoAuditoria.AuditQueried);
        await pagina.ClickAsync("button:has-text('Filtrar')");
        await AguardarRodapeAsync(pagina, "1–20 de 20");

        // Limpar restaura o filtro vazio (a trilha do tenant inteiro volta, sempre só do tenant A).
        await pagina.ClickAsync("button:has-text('Limpar')");
        await pagina.WaitForFunctionAsync("() => document.querySelector(\"input[aria-label='Ator']\").value === ''");
    }

    // ---------------------------------------------------------------------------------------------
    // Upload / Download (ex-placeholders Upload_Validado / Download_Autorizado)
    // ---------------------------------------------------------------------------------------------

    [SkippableFact] // EndpointsMarca.DefinirLogoContribuinteAsync: magic bytes PNG/JPEG, até 10 MB, admin.tenant, auditoria
    public async Task Upload_Validado_Da_Logo_Do_Contribuinte()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);
        await using var sessao = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A);
        var pagina = sessao.Pagina;
        var campo = "form[action='/admin/marca/contribuinte'] input[type='file']";
        var enviar = "form[action='/admin/marca/contribuinte'] button[type='submit']";

        // (a) Conteúdo que NÃO é imagem, com extensão .png → recusado pelos magic bytes (erro=3).
        // A página é interativa: só escolhe o arquivo depois que o circuito renderizou e estabilizou (senão o input
        // recriado pelo re-render perde o arquivo e o "required" nativo bloqueia o envio).
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => IrAsync(pagina, "/admin/marca"));
        await EnviarLogoPelaTelaAsync(pagina, campo, enviar, new FilePayload { Name = "falso.png", MimeType = "image/png", Buffer = "não sou imagem"u8.ToArray() });
        pagina.Url.Should().Contain("erro=3");
        (await pagina.Locator("[role='alert']").InnerTextAsync()).Should().Contain("Formato não suportado");

        // (b) Acima do limite (10 MB) → recusado (erro=2).
        var grande = new byte[MarcaVisual.TamanhoMaximoBytes + 1];
        PngValido.CopyTo(grande, 0);
        await EnviarLogoPelaTelaAsync(pagina, campo, enviar, new FilePayload { Name = "grande.png", MimeType = "image/png", Buffer = grande });
        pagina.Url.Should().Contain("erro=2");
        (await ContarAuditoriaAsync(cenario.A.TenantId, AcaoAuditoria.MarcaTenantAtualizada)).Should().Be(0);

        // (c) PNG válido maior que o antigo limite de 512 KB (1 MB) → aceito (ok=contribuinte), auditado, e servido ao
        //     próprio tenant.
        var png1Mb = new byte[1024 * 1024];
        PngValido.CopyTo(png1Mb, 0);
        await EnviarLogoPelaTelaAsync(pagina, campo, enviar, new FilePayload { Name = "logo.png", MimeType = "image/png", Buffer = png1Mb });
        pagina.Url.Should().Contain("ok=contribuinte");
        (await ContarAuditoriaAsync(cenario.A.TenantId, AcaoAuditoria.MarcaTenantAtualizada)).Should().Be(1);
        await pagina.Locator("img[alt='Logo do contribuinte']").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    [SkippableFact] // EndpointsMarca.ServirLogoContribuinteAsync: RequireAuthorization + tenant da sessão (fail-closed)
    public async Task Download_Autorizado_Da_Logo_Somente_Do_Proprio_Tenant()
    {
        _ambiente.Exigir();
        var cenario = await SemeadorRoundTripE2E.SemearAsync(_ambiente);

        // B configura a SUA logo pelo fluxo real (POST multipart com antiforgery da própria sessão).
        await using (var sessaoB = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.B))
        {
            (await PostarLogoContribuinteAsync(sessaoB.Pagina, PngValido, "logo-b.png")).Should().Contain("ok=contribuinte");

            var respostaB = await sessaoB.Contexto.APIRequest.GetAsync("/branding/logo/contribuinte");
            respostaB.Status.Should().Be(200, "o dono baixa a própria logo");
            respostaB.Headers["content-type"].Should().StartWith("image/png", "o tipo vem dos magic bytes, não do cliente");
            (await respostaB.BodyAsync()).Should().Equal(PngValido);

            // Cache: com o ETag da resposta, o navegador recebe 304 (sem corpo) até a logo mudar.
            var etag = respostaB.Headers["etag"];
            etag.Should().NotBeNullOrWhiteSpace();
            var condicional = await sessaoB.Contexto.APIRequest.GetAsync("/branding/logo/contribuinte",
                new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["If-None-Match"] = etag } });
            condicional.Status.Should().Be(304, "a mesma logo não é baixada de novo");
        }

        // A (sem logo própria) NUNCA recebe a logo de B — nem por fallback entre contribuintes.
        await using (var sessaoA = await SessaoBrowserE2E.AutenticarAsync(Navegador, _ambiente.BaseUrl, cenario.A))
        {
            var respostaA = await sessaoA.Contexto.APIRequest.GetAsync("/branding/logo/contribuinte");
            respostaA.Status.Should().Be(404, "a logo é resolvida pelo tenant da sessão — B não vaza para A");
        }

        // Anônimo: exige sessão (desafio do cookie), sem bytes de imagem.
        await using var anonimo = await SessaoBrowserE2E.NovaSessaoAsync(Navegador, _ambiente.BaseUrl);
        var respostaAnonima = await anonimo.Contexto.APIRequest.GetAsync("/branding/logo/contribuinte",
            new APIRequestContextOptions { MaxRedirects = 0 });
        respostaAnonima.Status.Should().Be(302, "sem sessão o servidor desafia a autenticação");
        respostaAnonima.Headers.TryGetValue("content-type", out var tipoAnonimo);
        (tipoAnonimo ?? string.Empty).Should().NotStartWith("image/");
    }

    // ---------------------------------------------------------------------------------------------
    // Apoio
    // ---------------------------------------------------------------------------------------------

    // Escolhe o arquivo no input REAL e envia o formulário nativo; aguarda a página de resultado (nova página interativa)
    // renderizar e estabilizar no circuito.
    private static async Task EnviarLogoPelaTelaAsync(IPage pagina, string campo, string enviar, FilePayload arquivo)
    {
        await pagina.Locator(campo).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await pagina.SetInputFilesAsync(campo, arquivo);
        await InteracaoBlazorE2E.ExecutarEAguardarCircuitoAsync(pagina, () => pagina.ClickAsync(enviar));
    }

    private static async Task<IResponse?> IrAsync(IPage pagina, string rota) =>
        await pagina.GotoAsync(rota, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

    private static async Task<ILocator> AguardarLinhaAsync(IPage pagina, string textoDaLinha)
    {
        var linha = pagina.Locator($"tr:has-text('{textoDaLinha}')");
        await linha.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        return linha;
    }

    private static Task AguardarRodapeAsync(IPage pagina, string trecho) =>
        pagina.Locator($".dt-foot:has-text('{trecho}')").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

    private static async Task<List<string>> DatasDasLinhasAsync(IPage pagina) =>
        [.. (await pagina.Locator("table.dt tbody tr td:first-child").AllInnerTextsAsync()).Select(t => t.Trim())];

    // Mesmo formato da tela (dd/MM/yyyy HH:mm, UTC). Todas as datas semeadas estão no MESMO dia, então a ordem
    // textual coincide com a cronológica.
    private static string Formatar(DateTimeOffset quando) => quando.UtcDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    // Código de 6 dígitos que NÃO é válido em nenhuma janela aceitável (±2 passos de 30 s).
    private static string CodigoInvalido(string segredoBase32)
    {
        var agora = DateTimeOffset.UtcNow;
        var validos = Enumerable.Range(-2, 5)
            .Select(passo => CodigoTotpTeste.Gerar(segredoBase32, agora.AddSeconds(30 * passo)))
            .ToHashSet(StringComparer.Ordinal);
        for (var n = 0; ; n++)
        {
            var candidato = n.ToString("D6", CultureInfo.InvariantCulture);
            if (!validos.Contains(candidato))
            {
                return candidato;
            }
        }
    }

    // Envia o formulário real de upload da logo do contribuinte (multipart + antiforgery da PRÓPRIA sessão, lido de um
    // formulário renderizado pelo servidor). Devolve a URL final após o redirect (ok=/erro=).
    private static async Task<string> PostarLogoContribuinteAsync(IPage pagina, byte[] conteudo, string nomeArquivo)
    {
        await IrAsync(pagina, "/conta/sair"); // página com formulário + <AntiforgeryToken /> do usuário logado
        return await pagina.EvaluateAsync<string>(
            @"async ([bytes, nome]) => {
                const token = document.querySelector('input[name=""__RequestVerificationToken""]').value;
                const fd = new FormData();
                fd.append('__RequestVerificationToken', token);
                fd.append('arquivo', new Blob([new Uint8Array(bytes)], { type: 'image/png' }), nome);
                const r = await fetch('/admin/marca/contribuinte', { method: 'POST', body: fd, redirect: 'follow' });
                return r.url;
            }",
            new object[] { conteudo.Select(b => (int)b).ToArray(), nomeArquivo })
            .WaitAsync(TimeSpan.FromSeconds(30)); // evaluate não tem timeout próprio no Playwright
    }

    private ISecureDataFormat<AuthenticationTicket> FormatoTicket() =>
        _ambiente.ServicosWeb.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;

    // Reconstrói o valor do cookie de sessão (o ChunkingCookieManager pode dividi-lo em portalbase.authC1..Cn).
    private static async Task<string> LerCookieSessaoAsync(IBrowserContext contexto)
    {
        var cookies = await contexto.CookiesAsync();
        var principal = cookies.Single(c => c.Name == CookieSessao).Value;
        if (!principal.StartsWith("chunks-", StringComparison.Ordinal))
        {
            return principal;
        }

        var partes = int.Parse(principal["chunks-".Length..], CultureInfo.InvariantCulture);
        return string.Concat(Enumerable.Range(1, partes).Select(i => cookies.Single(c => c.Name == CookieSessao + "C" + i.ToString(CultureInfo.InvariantCulture)).Value));
    }

    private static async Task SubstituirCookieSessaoAsync(IBrowserContext contexto, string valor)
    {
        var atual = (await contexto.CookiesAsync()).Single(c => c.Name == CookieSessao);
        await contexto.ClearCookiesAsync(new BrowserContextClearCookiesOptions { NameRegex = new System.Text.RegularExpressions.Regex("^" + CookieSessao) });
        await contexto.AddCookiesAsync([new Cookie
        {
            Name = CookieSessao,
            Value = valor,
            Domain = atual.Domain,
            Path = atual.Path,
            HttpOnly = true,
            SameSite = SameSiteAttribute.Lax,
        }]);
    }

    private async Task<int> ContarAuditoriaAsync(Guid tenantId, string operacao)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        return await catalogo.RegistrosAuditoria.AsNoTracking()
            .CountAsync(r => r.Operacao == operacao && r.Tenant == tenantId.ToString());
    }

    private async Task<List<string>> PapeisDoVinculoAsync(Guid usuarioId, Guid tenantId)
    {
        using var escopo = _ambiente.CriarEscopoSuporte();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        return await (
            from vinculo in catalogo.UsuarioTenants.AsNoTracking()
            join vp in catalogo.UsuarioTenantPapeis.AsNoTracking() on vinculo.Id equals vp.UsuarioTenantId
            join papel in catalogo.Papeis.AsNoTracking() on vp.PapelId equals papel.Id
            where vinculo.UsuarioId == usuarioId && vinculo.TenantId == tenantId
            select papel.Nome).ToListAsync();
    }

    // Espera por CONDIÇÃO observável (estado real no MySQL), não por sleep de sincronização.
    private static async Task<bool> AguardarAsync(Func<Task<bool>> condicao)
    {
        for (var tentativa = 0; tentativa < MaxTentativas; tentativa++)
        {
            if (await condicao())
            {
                return true;
            }

            await Task.Delay(IntervaloMs);
        }

        return await condicao();
    }
}
