using System.Text.RegularExpressions;

namespace PortalBase.Application.Auditoria;

/// <summary>
/// Sanitização CENTRALIZADA de valores de auditoria/log (seções 6, 7, 13). Redige valores de
/// chaves sensíveis (senha, token, segredo, MFA, certificado, connection string, credencial)
/// e trunca valores longos — para que a trilha jamais vire cópia de dados sensíveis. Regra
/// única, em um único lugar (não espalhar mascaramento por dezenas de classes).
/// </summary>
public static partial class SanitizacaoAuditoria
{
    public const string Redigido = "***";
    private const int LimiteValor = 512;

    /// <summary>Chaves cujo VALOR nunca pode ser persistido em claro (redação total).</summary>
    private static readonly string[] ChavesProibidas =
    [
        "senha", "password", "hash", "token", "segredo", "secret", "mfa", "otp", "totp",
        "recovery", "recuperacao", "cookie", "authorization", "credential", "credencial",
        "connectionstring", "connstring", "pfx", "privatekey", "certificate", "certificado",
        "clientsecret", "apikey", "bearer",
    ];

    /// <summary>Sanitiza um dicionário de valores (old/new/metadata): redige chaves sensíveis e trunca.</summary>
    public static IReadOnlyDictionary<string, string?> Sanitizar(IReadOnlyDictionary<string, string?>? origem)
    {
        if (origem is null || origem.Count == 0)
        {
            return EmptyReadOnly.Instance;
        }

        var destino = new Dictionary<string, string?>(origem.Count, StringComparer.Ordinal);
        foreach (var (chave, valor) in origem)
        {
            destino[chave] = ChaveEhSensivel(chave) ? Redigido : Truncar(valor);
        }

        return destino;
    }

    /// <summary>Sanitiza um texto livre (motivo/mensagem): remove padrões sensíveis e trunca.</summary>
    public static string? SanitizarTexto(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return texto;
        }

        var limpo = PadraoSensivel().Replace(texto, Redigido);
        return Truncar(limpo);
    }

    public static bool ChaveEhSensivel(string chave)
    {
        ArgumentNullException.ThrowIfNull(chave);
        foreach (var proibida in ChavesProibidas)
        {
            if (chave.Contains(proibida, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Truncar(string? valor)
        => valor is { Length: > LimiteValor } ? valor[..LimiteValor] : valor;

    // Redige atribuições "chave=valor" / "chave: valor" cujas chaves sejam sensíveis.
    [GeneratedRegex(@"(?i)\b(senha|password|token|secret|segredo|mfa|otp|authorization|bearer|connectionstring|client_?secret|api_?key|private_?key)\b\s*[:=]\s*\S+")]
    private static partial Regex PadraoSensivel();

    private static class EmptyReadOnly
    {
        public static readonly IReadOnlyDictionary<string, string?> Instance =
            new Dictionary<string, string?>(StringComparer.Ordinal);
    }
}
