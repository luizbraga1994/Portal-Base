using Microsoft.AspNetCore.DataProtection;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Guarda o código do link de redefinição num cookie PROTEGIDO (Data Protection, com validade) assim que o link do
/// e-mail é aberto, para a página seguir num endereço SEM o código: ele não volta para URLs, redirecionamentos,
/// histórico do navegador nem logs de acesso (regra 8). HttpOnly, SameSite=Strict, só em /conta.
/// </summary>
public static class CookieRedefinicaoSenha
{
    public const string Nome = "portalbase.redefinicao";
    private const string Proposito = "PortalBase.RedefinicaoSenha.v1";
    private const string Caminho = "/conta";
    private const int TamanhoMaximoToken = 512;

    public static void Guardar(HttpContext contexto, IDataProtectionProvider protecao, string token, TimeSpan validade)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(protecao);
        if (string.IsNullOrWhiteSpace(token) || token.Length > TamanhoMaximoToken)
        {
            Remover(contexto);
            return;
        }

        var protegido = protecao.CreateProtector(Proposito).ToTimeLimitedDataProtector().Protect(token.Trim(), validade);
        contexto.Response.Cookies.Append(Nome, protegido, new CookieOptions
        {
            HttpOnly = true,
            Secure = contexto.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = Caminho,
            MaxAge = validade,
            IsEssential = true,
        });
    }

    /// <summary>O código guardado, ou <c>null</c> se não há cookie, ele expirou ou foi adulterado.</summary>
    public static string? Ler(HttpContext contexto, IDataProtectionProvider protecao)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(protecao);
        if (!contexto.Request.Cookies.TryGetValue(Nome, out var protegido) || string.IsNullOrEmpty(protegido))
        {
            return null;
        }

        try
        {
            return protecao.CreateProtector(Proposito).ToTimeLimitedDataProtector().Unprotect(protegido);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // expirado, adulterado ou de outra chave
        }
    }

    public static void Remover(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        contexto.Response.Cookies.Delete(Nome, new CookieOptions { Path = Caminho, HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = contexto.Request.IsHttps });
    }
}
