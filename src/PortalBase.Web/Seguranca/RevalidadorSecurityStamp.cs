using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Revalida periodicamente a sessão Blazor Server comparando o <c>SecurityStamp</c> do cookie
/// com o do usuário no catálogo (seção 28). Se o usuário foi desativado, teve o acesso revogado,
/// trocou a senha ou o MFA, a marca diverge e a sessão é invalidada — mesmo com o circuito
/// SignalR aberto. É a estratégia de revogação compatível com Blazor Server.
/// </summary>
public sealed class RevalidadorSecurityStamp : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _escopos;

    public RevalidadorSecurityStamp(ILoggerFactory loggerFactory, IServiceScopeFactory escopos)
        : base(loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(escopos);
        _escopos = escopos;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);

        var principal = authenticationState.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var usuarioId)
            || !Guid.TryParse(principal.FindFirstValue(ClaimsPortalBase.SecurityStamp), out var stampSessao))
        {
            return false;
        }

        await using var escopo = _escopos.CreateAsyncScope();
        var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioUsuario>();
        var usuario = await repositorio.ObterPorIdAsync(usuarioId, cancellationToken).ConfigureAwait(false);

        return usuario is not null
            && usuario.Status == StatusUsuario.Ativo
            && usuario.SecurityStamp == stampSessao;
    }
}
