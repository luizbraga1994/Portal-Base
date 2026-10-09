using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using PortalBase.Application.Autorizacao;

namespace PortalBase.Web.Seguranca;

/// <summary>Nomes de policies e limitadores centralizados (evita strings soltas).</summary>
public static class PoliticasWeb
{
    public const string MfaSatisfeito = "mfa-satisfeito";
    public const string RateLogin = "rate-login";
    public const string RateMfa = "rate-mfa";
    public const string RateUpload = "rate-upload";
    public const string RateRecuperacaoSenha = "rate-recuperacao-senha";
}

/// <summary>
/// Composição de segurança do host Web (Prompt 04): autenticação por cookie SEGURO,
/// autorização server-side, revalidação de sessão (revogação), rate limiting em endpoints
/// sensíveis e contexto de segurança derivado das claims. Nada aqui depende do estado de UI.
/// </summary>
public static class ConfiguracaoSeguranca
{
    public static IServiceCollection AddSegurancaIdentidade(
        this IServiceCollection services, IWebHostEnvironment ambiente, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ambiente);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();

        // O contexto de segurança do host vem das CLAIMS (servidor), não do estado Blazor.
        // Substitui a implementação padrão registrada pela Application (seções 9, 42).
        // Portador do principal no circuito Blazor (sem HttpContext nos eventos interativos): semeado
        // no início do render a partir do cookie já validado. O contexto de segurança lê dele quando
        // não há requisição HTTP, mantendo a fonte de verdade no servidor.
        services.AddScoped<PortadorPrincipalCircuito>();
        services.AddScoped<IContextoSeguranca, ContextoSegurancaHttp>();
        services.AddScoped<SelecaoTenant>();

        // P62-R1: as páginas interativas (InteractiveServer) rodam num escopo de DI PRÓPRIO do circuito; este handler
        // hidrata nele principal/tenant/permissões a partir do AuthenticationStateProvider do servidor (fail-closed).
        // A MESMA instância é o CircuitHandler e o serviço que as páginas (PaginaPortal) aguardam antes de inicializar.
        services.AddScoped<HidratacaoCircuito>();
        services.AddScoped<CircuitHandler>(sp => sp.GetRequiredService<HidratacaoCircuito>());

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(opcoes =>
            {
                opcoes.Cookie.Name = "portalbase.auth";
                opcoes.Cookie.HttpOnly = true;                        // seção 31: nunca acessível ao JS
                opcoes.Cookie.SameSite = SameSiteMode.Lax;            // seção 8/24: mitiga CSRF
                opcoes.Cookie.SecurePolicy = ambiente.IsProduction()
                    ? CookieSecurePolicy.Always                        // seção 31: Secure em produção
                    : CookieSecurePolicy.SameAsRequest;
                opcoes.ExpireTimeSpan = TimeSpan.FromHours(8);
                opcoes.SlidingExpiration = true;                      // seção 8: renovação
                opcoes.LoginPath = "/conta/login";
                opcoes.LogoutPath = "/conta/logout";
                opcoes.AccessDeniedPath = "/conta/negado";
                // Seção 28: senha trocada/redefinida, usuário inativado ou bloqueado ⇒ o cookie antigo deixa de valer.
                opcoes.Events.OnValidatePrincipal = ValidacaoSessaoCookie.ValidarAsync;
            });

        services.AddAuthorization(opcoes =>
        {
            // Policy de step-up: exige MFA satisfeito NESTA sessão (seção 19) — ou dispensado pelo perfil
            // "Administrador da plataforma" (claim emitida pelo servidor no login). A decisão fina
            // por tenant/permissão é do IServicoAutorizacao (Application), chamado nos casos de uso.
            opcoes.AddPolicy(PoliticasWeb.MfaSatisfeito, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(contexto => ClaimsPortalBase.StepUpSatisfeito(contexto.User)));
        });

        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, RevalidadorSecurityStamp>();

        // Rate limiting por origem nos endpoints sensíveis (seção 32) — limites CONFIGURÁVEIS (Prompt 68) com
        // padrões idênticos ao anterior; validação fail-fast no startup (valores inválidos abortam a composição,
        // nunca relaxam o limite em silêncio). Não afeta o uso normal.
        var sessao = new OpcoesSessao();
        configuration.GetSection(OpcoesSessao.Secao).Bind(sessao);
        sessao.Validar();
        services.AddSingleton(sessao);

        var rate = new OpcoesRateLimit();
        configuration.GetSection(OpcoesRateLimit.Secao).Bind(rate);
        rate.Validar();
        services.AddSingleton(rate);

        services.AddRateLimiter(opcoes =>
        {
            opcoes.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            opcoes.AddPolicy(PoliticasWeb.RateLogin, contexto => LimitadorPorOrigem(contexto, rate.LoginPorJanela, rate.Janela));
            opcoes.AddPolicy(PoliticasWeb.RateMfa, contexto => LimitadorPorOrigem(contexto, rate.MfaPorJanela, rate.Janela));
            opcoes.AddPolicy(PoliticasWeb.RateUpload, contexto => LimitadorPorOrigem(contexto, rate.UploadPorJanela, rate.Janela));
            opcoes.AddPolicy(PoliticasWeb.RateRecuperacaoSenha, contexto => LimitadorPorOrigem(contexto, rate.RecuperacaoSenhaPorJanela, rate.Janela));
        });

        return services;
    }

    private static RateLimitPartition<string> LimitadorPorOrigem(HttpContext contexto, int permitido, TimeSpan janela)
    {
        var chave = contexto.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
        return RateLimitPartition.GetFixedWindowLimiter(chave, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitido,
            Window = janela,
            QueueLimit = 0,
        });
    }
}
