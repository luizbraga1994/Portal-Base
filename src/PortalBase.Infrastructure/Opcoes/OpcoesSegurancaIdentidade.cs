namespace PortalBase.Infrastructure.Opcoes;

/// <summary>
/// Chaves de proteção da identidade (seções 7, 18): a proteção do segredo MFA (AES-GCM) e o
/// hash determinístico de tokens/códigos (HMAC). As chaves NÃO ficam aqui — apenas a
/// REFERÊNCIA ao segredo no cofre (regra 7). Cada segredo é uma chave em Base64 (32 bytes).
/// </summary>
public sealed class OpcoesSegurancaIdentidade
{
    public const string Secao = "SegurancaIdentidade";

    /// <summary>Referência (cofre) da chave AES-256 que cifra o segredo TOTP em repouso.</summary>
    public string ChaveProtecaoMfaRef { get; set; } = string.Empty;

    /// <summary>Referência (cofre) da chave HMAC do hash determinístico de tokens/códigos.</summary>
    public string ChaveHashRef { get; set; } = string.Empty;
}
