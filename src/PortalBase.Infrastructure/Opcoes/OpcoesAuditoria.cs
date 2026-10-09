namespace PortalBase.Infrastructure.Opcoes;

/// <summary>
/// Configuração da trilha de auditoria (Prompt 05). A chave do HMAC de integridade NÃO fica
/// aqui — apenas a REFERÊNCIA ao segredo no cofre (regra 7). O segredo é uma chave Base64.
/// </summary>
public sealed class OpcoesAuditoria
{
    public const string Secao = "Auditoria";

    /// <summary>Referência (cofre) da chave HMAC usada no hash de integridade dos registros.</summary>
    public string ChaveIntegridadeRef { get; set; } = string.Empty;
}
