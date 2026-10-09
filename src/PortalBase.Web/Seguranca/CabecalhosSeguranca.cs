namespace PortalBase.Web.Seguranca;

/// <summary>
/// Cabeçalhos de segurança HTTP (Prompt 07, seções 5, 6, 26). A CSP é compatível com o Blazor
/// Server real (scripts próprios, WebSocket do SignalR) e evita <c>unsafe-eval</c>; o
/// <c>style-src 'unsafe-inline'</c> é necessário pelos estilos inline do Blazor/Bootstrap
/// (documentado em docs/SEGURANCA.md). Respostas sensíveis (autenticação) recebem
/// <c>Cache-Control: no-store</c> para não serem armazenadas por caches/proxies.
/// </summary>
public static class CabecalhosSeguranca
{
    private const string Csp =
        "default-src 'self'; " +
        "img-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; " +
        "connect-src 'self' ws: wss:; " +
        "font-src 'self'; " +
        "object-src 'none'; " +
        "frame-src 'none'; " +
        "base-uri 'self'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'";

    public static IApplicationBuilder UseCabecalhosSeguranca(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (contexto, next) =>
        {
            var cabecalhos = contexto.Response.Headers;
            cabecalhos["X-Content-Type-Options"] = "nosniff";
            cabecalhos["X-Frame-Options"] = "DENY";
            cabecalhos["Referrer-Policy"] = "no-referrer";
            cabecalhos["X-Permitted-Cross-Domain-Policies"] = "none";
            cabecalhos["Cross-Origin-Opener-Policy"] = "same-origin";
            cabecalhos["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            cabecalhos["Content-Security-Policy"] = Csp;

            // Conteúdo sensível (autenticação) nunca deve ser cacheado (seções 5, 26).
            if (contexto.Request.Path.StartsWithSegments("/conta", StringComparison.OrdinalIgnoreCase))
            {
                cabecalhos["Cache-Control"] = "no-store, no-cache, must-revalidate";
                cabecalhos["Pragma"] = "no-cache";
            }

            await next(contexto);
        });
    }
}
