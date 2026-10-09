using System.Text;

namespace PortalBase.Application.Segredos;

/// <summary>
/// Construção DETERMINÍSTICA dos nomes lógicos de segredos (seção 15). Os nomes são montados
/// SEMPRE no servidor a partir do ambiente e do identificador técnico do tenant — NUNCA de
/// valor vindo do cliente (seção 24) e NUNCA contendo dado sensível (CNPJ, e-mail, razão
/// social, senha). O formato respeita a nomenclatura do cofre (Azure Key Vault:
/// <c>^[0-9a-zA-Z-]+$</c>, ≤ 127 caracteres).
/// </summary>
public static class NomeSegredo
{
    private const string Prefixo = "portalbase";

    // ---- Segredos GLOBAIS (criptográficos) ----
    public static string AuditoriaHmac(string ambiente) => Compor(ambiente, "audit-hmac");

    public static string ProtecaoTotp(string ambiente) => Compor(ambiente, "security-totp-protection");

    public static string HashIdentidade(string ambiente) => Compor(ambiente, "security-identity-hash");

    /// <summary>Senha do SMTP definida pela tela da plataforma (Configurações → E-mail).</summary>
    public static string EmailSmtpSenha(string ambiente) => Compor(ambiente, "email-smtp-password");

    // ---- Segredos por TENANT ----

    /// <summary>Segredo de integração externa do tenant (modelo do padrão por tenant).</summary>
    public static string SegredoIntegracao(string ambiente, Guid tenantId) => ComporTenant(ambiente, tenantId, "integ-secret");

    private static string Compor(string ambiente, string categoria)
        => $"{Prefixo}-{Normalizar(ambiente)}-{categoria}";

    private static string ComporTenant(string ambiente, Guid tenantId, string categoria)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant é obrigatório para nome de segredo por tenant.", nameof(tenantId));
        }

        return $"{Prefixo}-{Normalizar(ambiente)}-tenant-{tenantId:N}-{categoria}";
    }

    // Restringe o ambiente ao conjunto seguro do cofre; defaults para "dev" se vazio.
    private static string Normalizar(string? ambiente)
    {
        if (string.IsNullOrWhiteSpace(ambiente))
        {
            return "dev";
        }

        var construtor = new StringBuilder(ambiente.Length);
        foreach (var caractere in ambiente.ToLowerInvariant())
        {
            construtor.Append(char.IsAsciiLetterOrDigit(caractere) ? caractere : '-');
        }

        return construtor.ToString();
    }
}
