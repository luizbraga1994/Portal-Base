using System.Security.Cryptography;

namespace PortalBase.Application.Backup;

/// <summary>
/// Integridade do artefato de backup (Prompt 50, Fase 12) — SHA-256 (algoritmo aprovado), sobre o
/// conteúdo, nunca sobre o filename. Usado para gravar o hash no catálogo e para VERIFICAR antes de
/// qualquer restauração (BACKUP ≠ RESTORE; nenhum backup é confiável sem integridade).
/// </summary>
public static class HashBackup
{
    /// <summary>Calcula o SHA-256 do stream (64 hex minúsculos). Não fecha o stream de entrada.</summary>
    public static async Task<string> CalcularAsync(Stream conteudo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(conteudo, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Compara dois hashes em tempo constante (evita vazamento por timing). Case-insensitive.</summary>
    public static bool Conferem(string? esperado, string? obtido)
    {
        if (string.IsNullOrEmpty(esperado) || string.IsNullOrEmpty(obtido) || esperado.Length != obtido.Length)
        {
            return false;
        }

        var a = System.Text.Encoding.ASCII.GetBytes(esperado.ToLowerInvariant());
        var b = System.Text.Encoding.ASCII.GetBytes(obtido.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
