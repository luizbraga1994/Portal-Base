using System.Security.Claims;
using PortalBase.Application.Autorizacao;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Contexto de segurança derivado NO SERVIDOR da sessão autenticada (o cookie validado), nunca do
/// estado de componente Blazor (seções 9, 42). O principal e o "MFA satisfeito" vêm exclusivamente das
/// claims do <see cref="ClaimsPrincipal"/> emitido pelo login/step-up — o navegador não consegue forjá-los.
///
/// Durante uma requisição HTTP (render estático/prerender, endpoints) a fonte é o <see cref="HttpContext"/>.
/// Nos eventos INTERATIVOS do Blazor Server não existe <see cref="HttpContext"/>; a fonte então é o
/// <see cref="PortadorPrincipalCircuito"/>, semeado no início do render a partir do mesmo cookie validado.
/// </summary>
public sealed class ContextoSegurancaHttp : IContextoSeguranca
{
    private readonly IHttpContextAccessor _acessor;
    private readonly PortadorPrincipalCircuito _portador;

    public ContextoSegurancaHttp(IHttpContextAccessor acessor, PortadorPrincipalCircuito portador)
    {
        ArgumentNullException.ThrowIfNull(acessor);
        ArgumentNullException.ThrowIfNull(portador);
        _acessor = acessor;
        _portador = portador;
    }

    private ClaimsPrincipal? Principal => _acessor.HttpContext?.User ?? _portador.Principal;

    public bool EstaAutenticado => Principal?.Identity?.IsAuthenticated == true;

    public string? PrincipalId => Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

    // Step-up feito (MFA) ou dispensado pelo perfil "Administrador da plataforma" — ambos emitidos pelo servidor.
    public bool MfaSatisfeito => ClaimsPortalBase.StepUpSatisfeito(Principal);
}
