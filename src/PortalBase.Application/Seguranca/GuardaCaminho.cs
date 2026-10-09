using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Seguranca;

/// <summary>
/// Proteção contra PATH TRAVERSAL e nomes de arquivo perigosos (Prompt 07, seção 15). Todo
/// upload/download/exportação futuro DEVE validar nomes aqui e preferir nomes GERADOS pelo
/// servidor. A entrada deve chegar já decodificada (a decodificação de URL é do host). Este
/// prompt prepara o controle — o módulo de upload é posterior.
/// </summary>
public static class GuardaCaminho
{
    private const int LimiteNome = 255;
    private static readonly char[] SeparadoresProibidos = ['/', '\\'];

    /// <summary>Um nome de arquivo é seguro (sem traversal, separador, raiz, controle ou caractere inválido)?</summary>
    public static bool NomeValido(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome) || nome.Length > LimiteNome)
        {
            return false;
        }

        if (nome.Contains("..", StringComparison.Ordinal)
            || nome.IndexOfAny(SeparadoresProibidos) >= 0
            || Path.IsPathRooted(nome)
            || nome.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        foreach (var caractere in nome)
        {
            if (char.IsControl(caractere))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Valida o nome ou lança (fail-closed).</summary>
    public static string ValidarNome(string? nome)
        => NomeValido(nome) ? nome! : throw new EntradaInvalidaException("Nome de arquivo inválido.");

    /// <summary>Nome de arquivo GERADO pelo servidor (nunca do usuário), com extensão validada.</summary>
    public static string GerarNome(string extensao)
    {
        var ext = NormalizarExtensao(extensao);
        return $"{Guid.NewGuid():N}{ext}";
    }

    /// <summary>
    /// Sanitiza um nome ORIGINAL para EXIBIÇÃO/metadado (nunca para caminho físico): extrai o
    /// nome-base (descarta qualquer diretório), remove controle e trunca. Nunca lança — devolve
    /// um rótulo seguro (ou "arquivo" se nada sobrar).
    /// </summary>
    public static string SanitizarNomeExibicao(string? nomeOriginal)
    {
        if (string.IsNullOrWhiteSpace(nomeOriginal))
        {
            return "arquivo";
        }

        // Descarta diretórios de qualquer estilo (barra ou contrabarra).
        var baseNome = nomeOriginal;
        var ultimoSeparador = baseNome.LastIndexOfAny(SeparadoresProibidos);
        if (ultimoSeparador >= 0)
        {
            baseNome = baseNome[(ultimoSeparador + 1)..];
        }

        var construtor = new System.Text.StringBuilder(baseNome.Length);
        foreach (var caractere in baseNome)
        {
            construtor.Append(char.IsControl(caractere) ? '_' : caractere);
        }

        var limpo = construtor.ToString().Trim().Trim('.');
        if (limpo.Length == 0)
        {
            return "arquivo";
        }

        return limpo.Length > LimiteNome ? limpo[..LimiteNome] : limpo;
    }

    /// <summary>Extensão (minúscula, sem ponto) de um nome, ou vazio. Não confia em separadores.</summary>
    public static string ExtrairExtensao(string? nomeOriginal)
    {
        if (string.IsNullOrWhiteSpace(nomeOriginal))
        {
            return string.Empty;
        }

        var ponto = nomeOriginal.LastIndexOf('.');
        if (ponto < 0 || ponto == nomeOriginal.Length - 1)
        {
            return string.Empty;
        }

        var ext = nomeOriginal[(ponto + 1)..].ToLowerInvariant();
        foreach (var caractere in ext)
        {
            if (!char.IsAsciiLetterOrDigit(caractere))
            {
                return string.Empty;
            }
        }

        return ext.Length > 16 ? string.Empty : ext;
    }

    /// <summary>
    /// Combina um diretório base com um nome validado e garante que o resultado permaneça DENTRO
    /// do diretório base (defesa contra escape por traversal/absoluto). Lança se escapar.
    /// </summary>
    public static string CombinarDentroDe(string diretorioBase, string nome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diretorioBase);
        ValidarNome(nome);

        var raiz = Path.GetFullPath(diretorioBase);
        var completo = Path.GetFullPath(Path.Combine(raiz, nome));
        var raizComSeparador = raiz.EndsWith(Path.DirectorySeparatorChar) ? raiz : raiz + Path.DirectorySeparatorChar;

        if (!completo.StartsWith(raizComSeparador, StringComparison.Ordinal))
        {
            throw new EntradaInvalidaException("Caminho fora do diretório permitido.");
        }

        return completo;
    }

    private static string NormalizarExtensao(string extensao)
    {
        if (string.IsNullOrWhiteSpace(extensao))
        {
            return string.Empty;
        }

        var limpa = extensao.Trim().TrimStart('.').ToLowerInvariant();
        foreach (var caractere in limpa)
        {
            if (!char.IsAsciiLetterOrDigit(caractere))
            {
                throw new EntradaInvalidaException("Extensão de arquivo inválida.");
            }
        }

        return limpa.Length == 0 ? string.Empty : "." + limpa;
    }
}
