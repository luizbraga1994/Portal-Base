using System.Globalization;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Comum;

/// <summary>
/// Valor monetário com 2 casas decimais, compatível com <c>DECIMAL(19,2)</c>.
/// Imutável e baseado em <see cref="decimal"/> — NUNCA ponto flutuante — para
/// evitar erros de arredondamento em valores fiscais/contábeis.
/// O valor <c>default</c> é <see cref="Zero"/> (estado válido).
/// </summary>
public readonly record struct Dinheiro
{
    /// <summary>Casas decimais fixas (escala do DECIMAL(19,2)).</summary>
    public const int CasasDecimais = 2;

    // 19 dígitos com escala 2 => até 17 dígitos na parte inteira => |valor| < 10^17.
    private const decimal LimiteExclusivo = 100_000_000_000_000_000m;

    public Dinheiro(decimal valor)
    {
        if (decimal.Round(valor, CasasDecimais, MidpointRounding.ToEven) != valor)
        {
            throw new EntradaInvalidaException("Valor monetário não pode ter mais de 2 casas decimais.");
        }

        if (Math.Abs(valor) >= LimiteExclusivo)
        {
            throw new EntradaInvalidaException("Valor monetário excede a precisão suportada (DECIMAL(19,2)).");
        }

        Valor = valor;
    }

    public decimal Valor { get; }

    public static Dinheiro Zero => default;

    public bool EhNegativo => Valor < 0m;

    public Dinheiro Somar(Dinheiro outro) => new(Valor + outro.Valor);

    public Dinheiro Subtrair(Dinheiro outro) => new(Valor - outro.Valor);

    public override string ToString() => Valor.ToString("0.00", CultureInfo.InvariantCulture);
}
