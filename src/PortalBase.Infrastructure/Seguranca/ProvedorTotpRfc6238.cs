using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PortalBase.Application.Autorizacao;

namespace PortalBase.Infrastructure.Seguranca;

/// <summary>
/// TOTP (RFC 6238) sobre HMAC-SHA1 — o algoritmo PADRÃO de autenticadores (Google
/// Authenticator etc.). Não é criptografia própria: é a implementação da norma IETF sobre a
/// primitiva do framework. Passo de 30s, 6 dígitos, janela de tolerância de ±1 passo. A
/// comparação do código é em tempo constante. O segredo/código nunca são registrados.
/// </summary>
public sealed class ProvedorTotpRfc6238 : IProvedorTotp
{
    private const int Digitos = 6;
    private const int PassoSegundos = 30;
    private const int JanelaTolerancia = 1;
    private const int TamanhoSegredoBytes = 20; // 160 bits (recomendado pela RFC 4226)
    private const string AlfabetoBase32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GerarSegredo() => Base32Encode(RandomNumberGenerator.GetBytes(TamanhoSegredoBytes));

    public bool Validar(string segredoBase32, string codigo, DateTimeOffset agoraUtc)
        => ValidarObtendoPasso(segredoBase32, codigo, agoraUtc) is not null;

    public long? ValidarObtendoPasso(string segredoBase32, string codigo, DateTimeOffset agoraUtc)
    {
        if (string.IsNullOrWhiteSpace(segredoBase32) || string.IsNullOrWhiteSpace(codigo))
        {
            return null;
        }

        var codigoLimpo = codigo.Trim();
        if (codigoLimpo.Length != Digitos || !codigoLimpo.All(char.IsAsciiDigit))
        {
            return null;
        }

        byte[] chave;
        try
        {
            chave = Base32Decode(segredoBase32);
        }
        catch (FormatException)
        {
            return null;
        }

        var passoAtual = agoraUtc.ToUnixTimeSeconds() / PassoSegundos;
        var alvo = Encoding.ASCII.GetBytes(codigoLimpo);

        for (var deslocamento = -JanelaTolerancia; deslocamento <= JanelaTolerancia; deslocamento++)
        {
            var passo = passoAtual + deslocamento;
            var esperado = Encoding.ASCII.GetBytes(GerarCodigo(chave, passo));
            if (CryptographicOperations.FixedTimeEquals(esperado, alvo))
            {
                return passo; // passo que casou → chave de anti-replay (Prompt 68)
            }
        }

        return null;
    }

    public string GerarUriProvisionamento(string segredoBase32, string emissor, string conta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segredoBase32);
        var emissorEsc = Uri.EscapeDataString(emissor);
        var contaEsc = Uri.EscapeDataString(conta);
        return $"otpauth://totp/{emissorEsc}:{contaEsc}?secret={segredoBase32}&issuer={emissorEsc}&digits={Digitos}&period={PassoSegundos}&algorithm=SHA1";
    }

    private static string GerarCodigo(byte[] chave, long contador)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, contador);

        // RFC 6238 exige HMAC-SHA1; é o algoritmo interoperável dos autenticadores.
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms — SHA1 mandatório pela RFC 6238 (TOTP).
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

    private static string Base32Encode(byte[] dados)
    {
        var construtor = new StringBuilder((dados.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in dados)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                construtor.Append(AlfabetoBase32[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
        {
            construtor.Append(AlfabetoBase32[(buffer << (5 - bits)) & 0x1F]);
        }

        return construtor.ToString();
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
