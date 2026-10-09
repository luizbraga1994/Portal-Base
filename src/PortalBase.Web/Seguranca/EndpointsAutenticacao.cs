using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Application.Multitenancy;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Endpoints de autenticação (login/step-up MFA/logout) executados NO SERVIDOR. Toda decisão
/// (senha, lockout, MFA) é dos casos de uso da Application; aqui apenas se traduz o resultado
/// em uma sessão por cookie. Antiforgery obrigatório (seção 24), rate limiting (seção 32),
/// mensagens genéricas (anti-enumeração — seção 26) e destino de redirecionamento local (seção 25).
/// </summary>
public static class EndpointsAutenticacao
{
    public static IEndpointRouteBuilder MapEndpointsAutenticacao(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);
        var grupo = rotas.MapGroup("/conta");

        grupo.MapPost("/login", LoginAsync).RequireRateLimiting(PoliticasWeb.RateLogin);
        grupo.MapPost("/mfa", ValidarMfaAsync).RequireRateLimiting(PoliticasWeb.RateMfa);
        grupo.MapPost("/contribuinte", SelecionarContribuinteAsync);
        grupo.MapPost("/logout", LogoutAsync);
        // POSTs em caminhos PRÓPRIOS: as páginas (/conta/esqueci-senha, /conta/redefinir-senha) também aceitam POST de
        // formulário do Blazor, e o mesmo caminho seria ambíguo.
        grupo.MapPost("/solicitar-recuperacao", SolicitarRecuperacaoAsync).RequireRateLimiting(PoliticasWeb.RateRecuperacaoSenha);
        grupo.MapPost("/redefinir", RedefinirSenhaAsync).RequireRateLimiting(PoliticasWeb.RateRecuperacaoSenha);
        grupo.MapPost("/alterar-senha", AlterarSenhaAsync).RequireRateLimiting(PoliticasWeb.RateLogin);

        return rotas;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext contexto,
        IAntiforgery antiforgery,
        AutenticarUsuario autenticar,
        IResolvedorTenant resolvedor,
        IConsultaAcessosUsuario acessos,
        CancellationToken cancellationToken)
    {
        if (!await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        var formulario = await contexto.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var destino = RedirecionamentoSeguro.Local(formulario["returnUrl"]);

        try
        {
            var resultado = await autenticar
                .ExecutarAsync(formulario["email"], formulario["senha"], cancellationToken)
                .ConfigureAwait(false);

            // Login estabelece a sessão SEM o fator MFA satisfeito (step-up é separado — seção 19).
            var identidade = ClaimsDaIdentidade(resultado, mfaSatisfeito: false);

            // MFA exigido pelo administrador e ainda não cadastrado: antes de tudo, o cadastro do autenticador.
            if (resultado.CadastroMfaPendente)
            {
                await EmitirSessaoAsync(contexto, identidade).ConfigureAwait(false);
                return Results.Redirect("/conta/cadastrar-mfa");
            }

            if (resultado.MfaHabilitado)
            {
                await EmitirSessaoAsync(contexto, identidade).ConfigureAwait(false);
                return Results.Redirect($"/conta/verificar?returnUrl={Uri.EscapeDataString(destino)}");
            }

            // Autenticada a IDENTIDADE, o próximo passo é o contribuinte (um usuário pode ter vínculo com vários).
            return await ConcluirComContribuinteAsync(contexto, identidade, resultado.UsuarioId, destino, resolvedor, acessos, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CredenciaisInvalidasException)
        {
            // Resposta genérica: não revela usuário inexistente vs. senha incorreta vs. bloqueio.
            return Results.Redirect("/conta/entrar?erro=1");
        }
    }

    private static async Task<IResult> ValidarMfaAsync(
        HttpContext contexto,
        IAntiforgery antiforgery,
        GestaoMfa mfa,
        IResolvedorTenant resolvedor,
        IConsultaAcessosUsuario acessos,
        CancellationToken cancellationToken)
    {
        if (!await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        if (contexto.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(contexto.User.FindFirstValue(ClaimTypes.NameIdentifier), out var usuarioId))
        {
            return Results.Redirect("/conta/entrar");
        }

        var formulario = await contexto.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var destino = RedirecionamentoSeguro.Local(formulario["returnUrl"]);

        try
        {
            await mfa.ValidarAsync(usuarioId, formulario["codigo"].ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (MfaInvalidoException)
        {
            return Results.Redirect($"/conta/verificar?erro=1&returnUrl={Uri.EscapeDataString(destino)}");
        }

        // Reemite a sessão adicionando o fator MFA satisfeito (step-up concluído) e segue para o contribuinte.
        var stamp = contexto.User.FindFirstValue(ClaimsPortalBase.SecurityStamp) ?? string.Empty;
        var login = contexto.User.Identity?.Name ?? string.Empty;
        var administradorPlataforma = contexto.User.HasClaim(ClaimsPortalBase.DispensaMfa, ClaimsPortalBase.ValorDispensaPlataforma);
        // A senha temporária continua pendente depois do MFA (só some quando o usuário cria a definitiva).
        var trocaSenhaPendente = contexto.User.HasClaim(ClaimsPortalBase.Pendencia, ClaimsPortalBase.PendenciaTrocaSenha);
        var identidade = ClaimsDaIdentidade(
            new ResultadoAutenticacao(usuarioId, login, login, true, GuidOuVazio(stamp), administradorPlataforma, TrocaSenhaPendente: trocaSenhaPendente),
            mfaSatisfeito: true);
        return await ConcluirComContribuinteAsync(contexto, identidade, usuarioId, destino, resolvedor, acessos, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fim da autenticação (senha de usuário sem MFA cadastrado, ou step-up concluído). Com UM ÚNICO contribuinte
    /// vinculado e operacional, a seleção é feita aqui, pelas MESMAS verificações do POST /conta/contribuinte: o tenant
    /// sai da lista de vínculos ATIVOS do próprio usuário (nunca do cliente — regra 4) e é re-resolvido/autorizado no
    /// servidor, fail-closed e auditado, ANTES de entrar na sessão. Nenhum, vários, indisponível ou recusado: tela de
    /// seleção, com a sessão sem contribuinte (como antes). A troca pela barra superior continua abrindo a tela.
    /// </summary>
    private static async Task<IResult> ConcluirComContribuinteAsync(
        HttpContext contexto,
        IReadOnlyList<Claim> identidade,
        Guid usuarioId,
        string destino,
        IResolvedorTenant resolvedor,
        IConsultaAcessosUsuario acessos,
        CancellationToken cancellationToken)
    {
        var vinculos = await acessos.ListarDoUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        if (vinculos is [{ Operacional: true } unico])
        {
            if (await ResolverContribuinteAsync(resolvedor, usuarioId, unico.TenantId, cancellationToken).ConfigureAwait(false))
            {
                await EmitirSessaoAsync(contexto, ComContribuinte(identidade, unico.TenantId, unico.Nome)).ConfigureAwait(false);
                return Results.Redirect(destino);
            }
        }

        await EmitirSessaoAsync(contexto, identidade).ConfigureAwait(false);
        return Results.Redirect($"/conta/selecionar?returnUrl={Uri.EscapeDataString(destino)}");
    }

    /// <summary>Re-resolução fail-closed e auditada (seção 21). Recusa de autorização vira <c>false</c>, nunca acesso.</summary>
    private static async Task<bool> ResolverContribuinteAsync(
        IResolvedorTenant resolvedor, Guid usuarioId, Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await resolvedor.ResolverAsync(usuarioId.ToString(), tenantId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TenantNaoAutorizadoException)
        {
            return false;
        }
        catch (TenantNaoEncontradoException)
        {
            return false;
        }
        catch (NaoAutenticadoException)
        {
            return false;
        }
    }

    /// <summary>
    /// SELEÇÃO do contribuinte ativo (seção 13/14). Estabelecida NO SERVIDOR: valida antiforgery, exige
    /// sessão autenticada, confere que o tenant informado está entre os vínculos ATIVOS do próprio usuário
    /// (regra 4/10), RE-RESOLVE/autoriza o tenant (fail-closed, auditado) e só então PERSISTE a escolha
    /// reemitindo o cookie com a claim do tenant. O identificador vindo do formulário nunca autoriza sozinho.
    /// </summary>
    private static async Task<IResult> SelecionarContribuinteAsync(
        HttpContext contexto,
        IAntiforgery antiforgery,
        IResolvedorTenant resolvedor,
        IConsultaAcessosUsuario acessos,
        CancellationToken cancellationToken)
    {
        if (!await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        if (contexto.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(contexto.User.FindFirstValue(ClaimTypes.NameIdentifier), out var usuarioId))
        {
            return Results.Redirect("/conta/entrar");
        }

        // Defesa em profundidade (o guarda de pendências já barra): sem o código MFA ou sem o MFA exigido cadastrado,
        // nenhum contribuinte entra na sessão (SEC-001).
        if (GuardaPendenciasConta.Destino(contexto.User, "/conta/contribuinte") is { } pendente)
        {
            return Results.Redirect(pendente);
        }

        var formulario = await contexto.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var destino = RedirecionamentoSeguro.Local(formulario["returnUrl"]);

        if (!Guid.TryParse(formulario["tenantId"], out var tenantId) || tenantId == Guid.Empty)
        {
            return Results.Redirect($"/conta/selecionar?erro=1&returnUrl={Uri.EscapeDataString(destino)}");
        }

        // Só é possível escolher entre os contribuintes com vínculo ATIVO do próprio usuário.
        var acessosDoUsuario = await acessos.ListarDoUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        var escolhido = acessosDoUsuario.FirstOrDefault(a => a.TenantId == tenantId);
        if (escolhido is null)
        {
            return Results.Redirect($"/conta/selecionar?erro=1&returnUrl={Uri.EscapeDataString(destino)}");
        }

        // Valida vínculo/estado NO SERVIDOR (fail-closed) e AUDITA a resolução (seção 21). Só então persiste.
        try
        {
            await resolvedor.ResolverAsync(usuarioId.ToString(), tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return Results.Redirect($"/conta/selecionar?erro=2&returnUrl={Uri.EscapeDataString(destino)}");
        }

        // Reemite o cookie PRESERVANDO as claims da sessão (identidade, MFA, security-stamp).
        await EmitirSessaoAsync(contexto, ComContribuinte(contexto.User.Claims, tenantId, escolhido.Nome)).ConfigureAwait(false);
        return Results.Redirect(destino);
    }

    /// <summary>As claims informadas, (re)definindo o contribuinte selecionado (id + NOME exibido na barra superior). A
    /// claim é protegida pelo Data Protection do cookie.</summary>
    private static List<Claim> ComContribuinte(IEnumerable<Claim> claimsSessao, Guid tenantId, string nome)
    {
        var claims = claimsSessao
            .Where(c => c.Type is not (ClaimsPortalBase.TenantSelecionado or ClaimsPortalBase.TenantNome))
            .ToList();
        claims.Add(new Claim(ClaimsPortalBase.TenantSelecionado, tenantId.ToString()));
        var exibido = nome.ToUpperInvariant();
        claims.Add(new Claim(ClaimsPortalBase.TenantNome, exibido.Length <= 200 ? exibido : exibido[..200]));
        return claims;
    }

    private static async Task<IResult> LogoutAsync(HttpContext contexto, IAntiforgery antiforgery)
    {
        // Sessão VÁLIDA: só sai com o token antiforgery (anti-CSRF). Sessão já encerrada pelo servidor (marca de segurança
        // trocada — ex.: acabou de cadastrar o MFA — ou cookie vencido): o token, emitido para a sessão antiga, não confere
        // mais; não há sessão a proteger, então só limpa o cookie e segue (sem 400 para o usuário).
        if (contexto.User.Identity?.IsAuthenticated == true
            && !await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        await contexto.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);

        // Saída depois de cadastrar o MFA: o login mostra o aviso para entrar de novo com o código (valor fixo, nunca livre).
        var aviso = contexto.Request.HasFormContentType
            ? (await contexto.Request.ReadFormAsync().ConfigureAwait(false))["aviso"].ToString()
            : string.Empty;
        return string.Equals(aviso, "mfa-cadastrado", StringComparison.Ordinal)
            ? Results.Redirect("/conta/entrar?mfa=cadastrado")
            : Results.Redirect("/");
    }

    /// <summary>"Esqueci a senha": a resposta é a MESMA exista ou não o e-mail (anti-enumeração).</summary>
    private static async Task<IResult> SolicitarRecuperacaoAsync(
        HttpContext contexto, IAntiforgery antiforgery, RecuperacaoSenha recuperacao, CancellationToken cancellationToken)
    {
        if (!await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        var formulario = await contexto.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var resultado = await recuperacao.SolicitarAsync(formulario["email"], cancellationToken).ConfigureAwait(false);
        return Results.Redirect(resultado == ResultadoSolicitacaoRecuperacao.Indisponivel
            ? "/conta/esqueci-senha?indisponivel=1"
            : "/conta/esqueci-senha?enviado=1");
    }

    /// <summary>Redefinição pelo link do e-mail. Concluída, encerra qualquer sessão deste navegador e volta ao login.</summary>
    private static async Task<IResult> RedefinirSenhaAsync(
        HttpContext contexto, IAntiforgery antiforgery, IDataProtectionProvider protecaoDados, RecuperacaoSenha recuperacao, CancellationToken cancellationToken)
    {
        if (!await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        // O código vem do cookie protegido gravado ao abrir o link (nunca da URL nem do formulário).
        var formulario = await contexto.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var resultado = await recuperacao
            .RedefinirAsync(CookieRedefinicaoSenha.Ler(contexto, protecaoDados), formulario["novaSenha"], formulario["confirmacao"], cancellationToken)
            .ConfigureAwait(false);

        switch (resultado)
        {
            case ResultadoRedefinicaoSenha.Redefinida:
                CookieRedefinicaoSenha.Remover(contexto);
                await contexto.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
                return Results.Redirect("/conta/entrar?redefinida=1");
            case ResultadoRedefinicaoSenha.SenhasDiferentes:
                return Results.Redirect("/conta/redefinir-senha?erro=diferentes");
            case ResultadoRedefinicaoSenha.SenhaForaDaPolitica:
                return Results.Redirect("/conta/redefinir-senha?erro=politica");
            default:
                CookieRedefinicaoSenha.Remover(contexto);
                return Results.Redirect("/conta/redefinir-senha?erro=link");
        }
    }

    /// <summary>
    /// Troca de senha do usuário logado. O usuário é o do cookie autenticado (servidor), nunca um campo do formulário.
    /// Trocada a senha, a sessão ATUAL é reemitida com o novo security stamp; as demais são encerradas pela revalidação.
    /// </summary>
    private static async Task<IResult> AlterarSenhaAsync(
        HttpContext contexto, IAntiforgery antiforgery, AlterarSenha alterar, CancellationToken cancellationToken)
    {
        if (!await ValidarAntiforgeryAsync(contexto, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        if (contexto.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(contexto.User.FindFirstValue(ClaimTypes.NameIdentifier), out var usuarioId))
        {
            return Results.Redirect("/conta/entrar");
        }

        var formulario = await contexto.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        AlteracaoSenha alteracao;
        try
        {
            alteracao = await alterar
                .ExecutarAsync(usuarioId, formulario["senhaAtual"], formulario["novaSenha"], formulario["confirmacao"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NaoAutenticadoException)
        {
            await contexto.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return Results.Redirect("/conta/entrar");
        }

        if (alteracao.Resultado == ResultadoAlteracaoSenha.Alterada)
        {
            // Senha definitiva criada: a pendência de senha temporária deixa a sessão (o cadastro já foi limpo no domínio).
            var eraTemporaria = contexto.User.HasClaim(ClaimsPortalBase.Pendencia, ClaimsPortalBase.PendenciaTrocaSenha);
            var claims = contexto.User.Claims
                .Where(c => c.Type != ClaimsPortalBase.SecurityStamp
                    && !(c.Type == ClaimsPortalBase.Pendencia && c.Value == ClaimsPortalBase.PendenciaTrocaSenha))
                .ToList();
            claims.Add(new Claim(ClaimsPortalBase.SecurityStamp, alteracao.NovoSecurityStamp!.Value.ToString()));
            await EmitirSessaoAsync(contexto, claims).ConfigureAwait(false);
            return Results.Redirect(eraTemporaria ? "/" : "/conta/minha-conta?senha=alterada");
        }

        var erro = alteracao.Resultado switch
        {
            ResultadoAlteracaoSenha.SenhaAtualIncorreta => "atual",
            ResultadoAlteracaoSenha.SenhasDiferentes => "diferentes",
            ResultadoAlteracaoSenha.MesmaSenha => "mesma",
            _ => "politica",
        };
        return Results.Redirect($"/conta/minha-conta?erro={erro}");
    }

    private static List<Claim> ClaimsDaIdentidade(ResultadoAutenticacao resultado, bool mfaSatisfeito)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, resultado.UsuarioId.ToString()),
            new(ClaimTypes.Name, resultado.Login),
            new(ClaimsPortalBase.SecurityStamp, resultado.SecurityStamp.ToString()),
        };

        if (mfaSatisfeito)
        {
            claims.Add(new Claim(ClaimsPortalBase.Amr, ClaimsPortalBase.ValorMfa));
        }

        // Perfil "Administrador da plataforma" (lido do catálogo no login): dispensa o step-up nesta sessão.
        if (resultado.AdministradorPlataforma)
        {
            claims.Add(new Claim(ClaimsPortalBase.DispensaMfa, ClaimsPortalBase.ValorDispensaPlataforma));
        }

        // Pendências da conta (do cadastro, no servidor): o guarda leva à tela de resolver antes de qualquer outra.
        // MFA cadastrado e código ainda não informado: a sessão da senha só serve para informar o código (SEC-001).
        if (resultado.MfaHabilitado && !mfaSatisfeito && !resultado.CadastroMfaPendente)
        {
            claims.Add(new Claim(ClaimsPortalBase.Pendencia, ClaimsPortalBase.PendenciaVerificacaoMfa));
        }

        if (resultado.CadastroMfaPendente)
        {
            claims.Add(new Claim(ClaimsPortalBase.Pendencia, ClaimsPortalBase.PendenciaCadastroMfa));
        }

        if (resultado.TrocaSenhaPendente)
        {
            claims.Add(new Claim(ClaimsPortalBase.Pendencia, ClaimsPortalBase.PendenciaTrocaSenha));
        }

        return claims;
    }

    private static Task EmitirSessaoAsync(HttpContext contexto, IEnumerable<Claim> claims)
        => contexto.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));

    private static async Task<bool> ValidarAntiforgeryAsync(HttpContext contexto, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(contexto).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static Guid GuidOuVazio(string valor) => Guid.TryParse(valor, out var g) ? g : Guid.Empty;
}
