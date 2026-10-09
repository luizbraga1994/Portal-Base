namespace PortalBase.Web.Seguranca;

/// <summary>
/// Proteção contra OPEN REDIRECT (seção 25): só aceita URLs LOCAIS como destino de
/// login/logout. Qualquer destino absoluto, protocol-relative (<c>//host</c>) ou com barra
/// invertida é descartado em favor de um destino local seguro.
/// </summary>
public static class RedirecionamentoSeguro
{
    public static string Local(string? returnUrl, string fallback = "/")
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return fallback;
        }

        // Precisa começar com uma única barra e não iniciar sequência de escape de host.
        var ehLocal = returnUrl.Length > 0
            && returnUrl[0] == '/'
            && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative);

        return ehLocal ? returnUrl : fallback;
    }
}
