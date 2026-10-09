using System.Security.Cryptography;
using PortalBase.Application.Autorizacao;

namespace PortalBase.Infrastructure.Seguranca;

/// <summary>
/// Geração de valores aleatórios de alta entropia com fonte CRIPTOGRÁFICA
/// (<see cref="RandomNumberGenerator"/>) — nunca <c>System.Random</c>. Usado por tokens de
/// recuperação (seção 27) e códigos de recuperação MFA (seção 36).
/// </summary>
public sealed class GeradorTokens : IGeradorTokens
{
    // Alfabeto sem caracteres ambíguos (0/O, 1/I/L) para códigos digitados por humanos.
    private const string AlfabetoCodigo = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int GruposCodigo = 2;
    private const int TamanhoGrupo = 5;

    public string GerarToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlSemPadding(bytes);
    }

    public string GerarCodigoRecuperacao()
    {
        var grupos = new string[GruposCodigo];
        for (var g = 0; g < GruposCodigo; g++)
        {
            var chars = new char[TamanhoGrupo];
            for (var i = 0; i < TamanhoGrupo; i++)
            {
                chars[i] = AlfabetoCodigo[RandomNumberGenerator.GetInt32(AlfabetoCodigo.Length)];
            }

            grupos[g] = new string(chars);
        }

        return string.Join('-', grupos);
    }

    public string GerarSenhaTemporaria()
    {
        const string Minusculas = "abcdefghjkmnpqrstuvwxyz";
        const string Maiusculas = "ABCDEFGHJKMNPQRSTUVWXYZ";
        const string Digitos = "23456789";
        const string Simbolos = "!@#$%&*-_+=?";
        const int Tamanho = 16;
        var todos = Minusculas + Maiusculas + Digitos + Simbolos;

        // Um de cada tipo (atende à política: 3 de 4 tipos e variedade) e o resto de todos, embaralhado.
        var chars = new char[Tamanho];
        chars[0] = Minusculas[RandomNumberGenerator.GetInt32(Minusculas.Length)];
        chars[1] = Maiusculas[RandomNumberGenerator.GetInt32(Maiusculas.Length)];
        chars[2] = Digitos[RandomNumberGenerator.GetInt32(Digitos.Length)];
        chars[3] = Simbolos[RandomNumberGenerator.GetInt32(Simbolos.Length)];
        for (var i = 4; i < Tamanho; i++)
        {
            chars[i] = todos[RandomNumberGenerator.GetInt32(todos.Length)];
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    private static string Base64UrlSemPadding(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
