using System.Text;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Catalogo;

/// <summary>
/// CNPJ do contribuinte. Objeto de valor imutável, persistido SOMENTE com dígitos.
/// Valida formato, tamanho e dígitos verificadores (regra 10). Identifica o
/// contribuinte, mas NÃO é usado como fonte de roteamento de banco (regra: o
/// roteamento usa o identificador interno do Tenant, resolvido pelo catálogo).
/// </summary>
public sealed record Cnpj
{
    public const int Tamanho = 14;

    private static readonly int[] PesosPrimeiroDigito = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
    private static readonly int[] PesosSegundoDigito = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    private Cnpj(string valor) => Valor = valor;

    /// <summary>14 dígitos, sem máscara.</summary>
    public string Valor { get; }

    /// <exception cref="EntradaInvalidaException">Vazio, tamanho incorreto ou dígitos verificadores inválidos.</exception>
    public static Cnpj Criar(string? entrada)
    {
        if (string.IsNullOrWhiteSpace(entrada))
        {
            throw new EntradaInvalidaException("CNPJ é obrigatório.");
        }

        var digitos = SomenteDigitos(entrada);

        if (digitos.Length != Tamanho)
        {
            throw new EntradaInvalidaException("CNPJ deve conter 14 dígitos.");
        }

        if (!DigitosVerificadoresValidos(digitos))
        {
            throw new EntradaInvalidaException("CNPJ inválido (dígitos verificadores).");
        }

        return new Cnpj(digitos);
    }

    private static string SomenteDigitos(string entrada)
    {
        var construtor = new StringBuilder(entrada.Length);
        foreach (var caractere in entrada)
        {
            if (char.IsAsciiDigit(caractere))
            {
                construtor.Append(caractere);
            }
        }

        return construtor.ToString();
    }

    private static bool DigitosVerificadoresValidos(string digitos)
    {
        // Sequências com todos os dígitos iguais passam na aritmética, mas são inválidas.
        if (TodosOsDigitosIguais(digitos))
        {
            return false;
        }

        var primeiro = CalcularDigito(digitos, PesosPrimeiroDigito);
        var segundo = CalcularDigito(digitos, PesosSegundoDigito);

        return digitos[12] == (char)('0' + primeiro)
            && digitos[13] == (char)('0' + segundo);
    }

    private static int CalcularDigito(string digitos, int[] pesos)
    {
        var soma = 0;
        for (var i = 0; i < pesos.Length; i++)
        {
            soma += (digitos[i] - '0') * pesos[i];
        }

        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }

    private static bool TodosOsDigitosIguais(string digitos)
    {
        for (var i = 1; i < digitos.Length; i++)
        {
            if (digitos[i] != digitos[0])
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Valor;
}
