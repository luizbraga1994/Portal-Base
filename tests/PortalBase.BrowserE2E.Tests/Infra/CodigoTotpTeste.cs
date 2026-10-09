using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace PortalBase.BrowserE2E.Tests.Infra;

/// <summary>
/// Prompt 76 — gera o código TOTP (RFC 6238) que um AUTENTICADOR real produziria, a partir do segredo
/// Base32 que o PRÓPRIO teste semeou (via <c>GestaoMfa.IniciarAtivacaoAsync</c>). NÃO é bypass de MFA:
/// o servidor continua validando o segundo fator pelo fluxo real (<c>POST /conta/mfa</c> → <c>GestaoMfa.ValidarAsync</c>);
/// o teste apenas conhece o segredo (porque o criou) e calcula o código do momento — exatamente o que o
/// app autenticador do usuário faria. Espelha o algoritmo de <c>ProvedorTotpRfc6238</c> (HMAC-SHA1, 6 dígitos,
/// passo de 30 s).
/// </summary>
internal static class CodigoTotpTeste
{
    private const int Digitos = 6;
    private const int PassoSegundos = 30;
    private const string AlfabetoBase32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Código TOTP de 6 dígitos válido no instante informado (mesmo passo de tempo do servidor).</summary>
    public static string Gerar(string segredoBase32, DateTimeOffset agoraUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segredoBase32);
        var chave = Base32Decode(segredoBase32);
        var passo = agoraUtc.ToUnixTimeSeconds() / PassoSegundos;

        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, passo);

        // RFC 6238 exige HMAC-SHA1 (algoritmo interoperável dos autenticadores) — mesmo do ProvedorTotpRfc6238.
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms — SHA1 é mandatório pela RFC 6238 (TOTP).
        var hash = HMACSHA1.HashData(chave, buffer.ToArray());
#pragma warning restore CA5350

        var offset = hash[^1] & 0x0F;
        var binario = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        var otp = binario % (int)Math.Pow(10, Digitos);
        return otp.ToString(CultureInfo.InvariantCulture).PadLeft(Digitos, '0');
    }

    private static byte[] Base32Decode(string base32)
    {
        var normalizado = base32.TrimEnd('=').ToUpperInvariant();
        var saida = new List<byte>(normalizado.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var caractere in normalizado)
        {
            var indice = AlfabetoBase32.IndexOf(caractere, StringComparison.Ordinal);
            if (indice < 0)
            {
                throw new FormatException("Segredo Base32 inválido.");
            }

            buffer = (buffer << 5) | indice;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                saida.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return [.. saida];
    }
}
