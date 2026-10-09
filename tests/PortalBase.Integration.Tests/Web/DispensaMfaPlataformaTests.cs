using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PortalBase.Web.Seguranca;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// A dispensa do segundo fator do perfil "Administrador da plataforma" vem de uma claim que SÓ o servidor emite no
/// login (cookie protegido). O contexto de segurança e a policy de step-up a reconhecem; sem ela (e sem "amr=mfa"),
/// nada de step-up. Claim com outro valor não vale.
/// </summary>
public sealed class DispensaMfaPlataformaTests
{
    private static ClaimsPrincipal Principal(params Claim[] extras)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), .. extras], "cookie"));

    private static ContextoSegurancaHttp Contexto(ClaimsPrincipal principal)
        => new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } }, new PortadorPrincipalCircuito());

    [Fact]
    public void Administrador_Da_Plataforma_Tem_O_Step_Up_Satisfeito()
    {
        var principal = Principal(new Claim(ClaimsPortalBase.DispensaMfa, ClaimsPortalBase.ValorDispensaPlataforma));

        ClaimsPortalBase.StepUpSatisfeito(principal).Should().BeTrue();
        Contexto(principal).MfaSatisfeito.Should().BeTrue();
    }

    [Fact]
    public void Mfa_Feito_Continua_Valendo()
        => Contexto(Principal(new Claim(ClaimsPortalBase.Amr, ClaimsPortalBase.ValorMfa))).MfaSatisfeito.Should().BeTrue();

    [Fact]
    public void Sem_Mfa_E_Sem_Dispensa_Nao_Ha_Step_Up()
    {
        Contexto(Principal()).MfaSatisfeito.Should().BeFalse();
        Contexto(Principal(new Claim(ClaimsPortalBase.DispensaMfa, "qualquer"))).MfaSatisfeito.Should().BeFalse("só o valor emitido pelo servidor vale");
        ClaimsPortalBase.StepUpSatisfeito(null).Should().BeFalse();
    }
}
