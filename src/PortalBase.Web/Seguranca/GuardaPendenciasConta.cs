namespace PortalBase.Web.Seguranca;

/// <summary>
/// Guarda de PENDÊNCIAS da conta (servidor): enquanto a sessão tiver uma pendência emitida no login, qualquer página ou
/// endpoint é redirecionado à tela que a resolve — <c>/conta/verificar</c> (código MFA ainda não informado),
/// <c>/conta/cadastrar-mfa</c> (MFA exigido e não cadastrado) ou <c>/conta/minha-conta</c> (senha temporária). Só o
/// necessário passa: a própria tela, sair/entrar, os arquivos do site e o canal do Blazor. A interface não decide nada:
/// sem resolver, nenhuma outra página é servida.
/// </summary>
public sealed class GuardaPendenciasConta
{
    // Caminhos liberados enquanto o código MFA não foi informado: a tela do código, o POST dele e sair/entrar. A seleção
    // do contribuinte NÃO está aqui (SEC-001).
    private static readonly string[] LiberadosVerificacaoMfa =
        ["/conta/verificar", "/conta/mfa", "/conta/logout", "/conta/sair", "/conta/entrar", "/conta/login"];

    // Caminhos liberados durante a pendência de MFA (o cadastro roda no circuito do Blazor).
    private static readonly string[] LiberadosCadastroMfa =
        ["/conta/cadastrar-mfa", "/conta/logout", "/conta/sair", "/conta/entrar", "/conta/login"];

    // Durante a senha temporária, toda a área /conta/ (verificar MFA, escolher contribuinte, trocar a senha, sair).
    private const string AreaConta = "/conta/";

    private static readonly string[] Infraestrutura =
        ["/_framework", "/_blazor", "/_content", "/css", "/js", "/fonts", "/lib", "/branding", "/health", "/favicon", "/Error", "/not-found"];

    private readonly RequestDelegate _proximo;

    public GuardaPendenciasConta(RequestDelegate proximo)
    {
        ArgumentNullException.ThrowIfNull(proximo);
        _proximo = proximo;
    }

    public Task InvokeAsync(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        var usuario = contexto.User;
        if (usuario.Identity?.IsAuthenticated == true && Destino(usuario, contexto.Request.Path) is { } destino)
        {
            contexto.Response.Redirect(destino);
            return Task.CompletedTask;
        }

        return _proximo(contexto);
    }

    /// <summary>Para onde levar (ou nulo: a requisição segue). Exposto para teste.</summary>
    public static string? Destino(System.Security.Claims.ClaimsPrincipal usuario, PathString caminho)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        var rota = caminho.Value ?? "/";
        if (Infraestrutura.Any(p => rota.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || ArquivoNaRaiz(rota))
        {
            return null;
        }

        if (usuario.HasClaim(ClaimsPortalBase.Pendencia, ClaimsPortalBase.PendenciaVerificacaoMfa))
        {
            return LiberadosVerificacaoMfa.Any(p => rota.Equals(p, StringComparison.OrdinalIgnoreCase)) ? null : "/conta/verificar";
        }

        if (usuario.HasClaim(ClaimsPortalBase.Pendencia, ClaimsPortalBase.PendenciaCadastroMfa))
        {
            return LiberadosCadastroMfa.Any(p => rota.Equals(p, StringComparison.OrdinalIgnoreCase)) ? null : "/conta/cadastrar-mfa";
        }

        if (usuario.HasClaim(ClaimsPortalBase.Pendencia, ClaimsPortalBase.PendenciaTrocaSenha))
        {
            return rota.StartsWith(AreaConta, StringComparison.OrdinalIgnoreCase) ? null : "/conta/minha-conta?obrigatoria=1";
        }

        return null;
    }

    // Só arquivos na RAIZ do site (app.css, PortalBase.Web.styles.css...). Um "ponto" em qualquer outro lugar não libera:
    // "/dados/conflitos/x.css" é uma página com parâmetro, não um arquivo.
    private static bool ArquivoNaRaiz(string rota) => rota.LastIndexOf('/') == 0 && Path.HasExtension(rota);
}
