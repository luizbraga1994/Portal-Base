using System.Globalization;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Comum;

/// <summary>Guardas de invariantes internas do domínio (regras 1, 10).</summary>
internal static class Exigir
{
    public static string TextoObrigatorio(string? valor, string campo)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            throw new EntradaInvalidaException($"O campo '{campo}' é obrigatório.");
        }

        return valor.Trim();
    }

    public static string TamanhoMaximo(string valor, int maximo, string campo)
    {
        if (valor.Length > maximo)
        {
            var limite = maximo.ToString(CultureInfo.InvariantCulture);
            throw new EntradaInvalidaException($"O campo '{campo}' excede o tamanho máximo de {limite} caracteres.");
        }

        return valor;
    }

    public static string TextoObrigatorioComLimite(string? valor, int maximo, string campo)
        => TamanhoMaximo(TextoObrigatorio(valor, campo), maximo, campo);
}
