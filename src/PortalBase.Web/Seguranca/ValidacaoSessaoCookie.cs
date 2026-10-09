using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Revalida o cookie de sessão nas requisições HTTP (seção 28): o <c>SecurityStamp</c> do cookie precisa ser o do
/// usuário no catálogo, e o usuário precisa estar ativo e sem bloqueio. Senha trocada/redefinida, MFA alterado,
/// usuário inativado ou acesso revogado ⇒ o cookie antigo é recusado e a sessão encerrada (o circuito Blazor tem o
/// <see cref="RevalidadorSecurityStamp"/>; isto cobre as páginas e os POSTs). Para não consultar o banco a cada
/// requisição, a verificação ocorre no máximo uma vez por intervalo (<see cref="OpcoesSessao"/>, padrão 60 s) por
/// sessão (instante gravado numa claim protegida do próprio cookie). Falha de consulta ⇒ recusa (fail-closed).
/// </summary>
public static class ValidacaoSessaoCookie
{
    /// <summary>Claim com o instante (UTC, ticks) da última verificação no catálogo.</summary>
    public const string ClaimValidadoEm = "portalbase:sessao_validada_em";

    public static async Task ValidarAsync(CookieValidatePrincipalContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        var principal = contexto.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var servicos = contexto.HttpContext.RequestServices;
        var agora = servicos.GetRequiredService<IRelogio>().AgoraUtc;
        var intervalo = (servicos.GetService<OpcoesSessao>() ?? new OpcoesSessao()).Intervalo;
        if (long.TryParse(principal.FindFirstValue(ClaimValidadoEm), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= agora.UtcTicks
            && agora.UtcTicks - ticks < intervalo.Ticks)
        {
            return;
        }

        if (!await SessaoAindaValidaAsync(principal, servicos, agora, contexto.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            contexto.RejectPrincipal();
            await contexto.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return;
        }

        var claims = principal.Claims.Where(c => c.Type != ClaimValidadoEm).ToList();
        claims.Add(new Claim(ClaimValidadoEm, agora.UtcTicks.ToString(CultureInfo.InvariantCulture)));
        contexto.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        contexto.ShouldRenew = true;
    }

    private static async Task<bool> SessaoAindaValidaAsync(ClaimsPrincipal principal, IServiceProvider servicos, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var usuarioId)
            || !Guid.TryParse(principal.FindFirstValue(ClaimsPortalBase.SecurityStamp), out var stampSessao))
        {
            return false;
        }

        Usuario? usuario;
        try
        {
            usuario = await servicos.GetRequiredService<IRepositorioUsuario>().ObterPorIdAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return false; // catálogo indisponível: não dá para provar que a sessão vale ⇒ recusa
        }

        return usuario is not null && !usuario.EstaImpedidoDeAutenticar(agora) && usuario.SecurityStamp == stampSessao;
    }
}
