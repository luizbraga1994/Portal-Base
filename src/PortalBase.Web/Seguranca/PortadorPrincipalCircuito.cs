using System.Security.Claims;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Portador (escopo de circuito) do principal autenticado quando NÃO há <see cref="HttpContext"/> —
/// caso dos eventos interativos do Blazor Server (o circuito roda sobre o WebSocket, sem request HTTP).
/// É semeado no início do render por um componente-raiz a partir do <c>AuthenticationState</c> (o cookie
/// já validado). Assim o contexto de segurança do servidor funciona tanto em requisições HTTP quanto
/// dentro do circuito, sem confiar em estado enviado pelo cliente.
/// </summary>
public sealed class PortadorPrincipalCircuito
{
    public ClaimsPrincipal? Principal { get; private set; }

    public void Definir(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Principal = principal;
    }
}
