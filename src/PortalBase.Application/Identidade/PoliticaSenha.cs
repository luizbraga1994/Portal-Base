namespace PortalBase.Application.Identidade;

/// <summary>
/// Política de senha do portal (o domínio recebe apenas o HASH): usada na troca de senha, na redefinição pelo
/// "Esqueci a senha" e no comando de acesso local. Mínimo de 12 caracteres, 3 dos 4 tipos (minúsculas, maiúsculas,
/// dígitos, símbolos), variedade mínima de caracteres e sem conter o nome de usuário nem o e-mail.
/// </summary>
public static class PoliticaSenha
{
    public const int TamanhoMinimo = 12;
    public const int TamanhoMaximo = 128;
    private const int CaracteresDistintosMinimos = 6;

    /// <summary>
    /// Devolve o motivo da rejeição (texto fixo, seguro para exibir — nunca ecoa a senha), ou <c>null</c> se a senha
    /// atende à política. A senha não pode conter o nome de usuário nem a parte local do e-mail (antes do @).
    /// </summary>
    public static string? Validar(string? senha, string login, string? email = null)
    {
        ArgumentNullException.ThrowIfNull(login);

        if (string.IsNullOrEmpty(senha))
        {
            return "A senha não pode ser vazia.";
        }

        if (senha.Length < TamanhoMinimo || senha.Length > TamanhoMaximo)
        {
            return $"A senha precisa ter entre {TamanhoMinimo} e {TamanhoMaximo} caracteres.";
        }

        if (senha.Trim().Length != senha.Length)
        {
            return "A senha não pode começar nem terminar com espaço.";
        }

        var tipos = (senha.Any(char.IsLower) ? 1 : 0)
            + (senha.Any(char.IsUpper) ? 1 : 0)
            + (senha.Any(char.IsDigit) ? 1 : 0)
            + (senha.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (tipos < 3)
        {
            return "Use ao menos 3 destes 4 tipos: letras minúsculas, maiúsculas, dígitos e símbolos.";
        }

        if (senha.Distinct().Count() < CaracteresDistintosMinimos)
        {
            return "A senha repete caracteres demais.";
        }

        if (login.Length >= 3 && senha.Contains(login, StringComparison.OrdinalIgnoreCase))
        {
            return "A senha não pode conter o nome de usuário.";
        }

        var localEmail = email is null ? string.Empty : email.Split('@')[0].Trim();
        if (localEmail.Length >= 3 && senha.Contains(localEmail, StringComparison.OrdinalIgnoreCase))
        {
            return "A senha não pode conter a parte do seu e-mail antes do @.";
        }

        return null;
    }
}
