using Microsoft.AspNetCore.Identity;
using PortalBase.Application.Autorizacao;

namespace PortalBase.Infrastructure.Seguranca;

/// <summary>
/// Hashing de senha com o <see cref="PasswordHasher{TUser}"/> do ASP.NET Core Identity
/// (PBKDF2-HMAC-SHA512, formato versionado com salt por senha). Reusa a primitiva VETADA da
/// plataforma — nenhum algoritmo próprio (seção 5). A senha em claro nunca é persistida nem
/// registrada; o <c>TUser</c> não é usado pela implementação (passa-se um alvo neutro).
/// </summary>
public sealed class HasherSenhaIdentity : IHasherSenha
{
    private static readonly object Alvo = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Gerar(string senha)
    {
        ArgumentException.ThrowIfNullOrEmpty(senha);
        return _hasher.HashPassword(Alvo, senha);
    }

    public ResultadoVerificacaoSenha Verificar(string hashArmazenado, string senhaInformada)
    {
        if (string.IsNullOrEmpty(hashArmazenado) || string.IsNullOrEmpty(senhaInformada))
        {
            return ResultadoVerificacaoSenha.Falha;
        }

        PasswordVerificationResult resultado;
        try
        {
            resultado = _hasher.VerifyHashedPassword(Alvo, hashArmazenado, senhaInformada);
        }
        catch (FormatException)
        {
            // Hash armazenado malformado: trata como falha (fail-closed), sem vazar detalhe.
            return ResultadoVerificacaoSenha.Falha;
        }

        return resultado switch
        {
            PasswordVerificationResult.Success => ResultadoVerificacaoSenha.Sucesso,
            PasswordVerificationResult.SuccessRehashNeeded => ResultadoVerificacaoSenha.SucessoPrecisaRehash,
            _ => ResultadoVerificacaoSenha.Falha,
        };
    }
}
