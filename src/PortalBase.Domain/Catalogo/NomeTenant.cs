using System.Text.RegularExpressions;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Catalogo;

/// <summary>
/// Nome do tenant ESCOLHIDO na criação (ex.: <c>STAKE</c>): é o que aparece na seleção de contribuinte e define o nome
/// da base física (<see cref="NomeBaseFisica"/> = <c>portalbase_tenant_stake</c>). Sempre MAIÚSCULO; só letras sem
/// acento, dígitos e <c>_</c>, começando por letra (2 a 40). Esse alfabeto restrito é o que torna o nome seguro como
/// identificador de banco (regra 13 — DDL não aceita parâmetro). Único no catálogo e imutável depois de criado.
/// </summary>
public sealed partial record NomeTenant
{
    public const int TamanhoMinimo = 2;
    public const int TamanhoMaximo = 40;

    /// <summary>Prefixo fixo das bases de contribuinte; o MySQL limita o nome da base a 64 caracteres (18 + 40 cabe).</summary>
    public const string PrefixoBaseFisica = "portalbase_tenant_";

    private NomeTenant(string valor) => Valor = valor;

    /// <summary>O nome normalizado (MAIÚSCULO).</summary>
    public string Valor { get; }

    /// <summary>Nome da base individual do contribuinte: <c>portalbase_tenant_&lt;nome em minúsculas&gt;</c>.</summary>
    public string NomeBaseFisica => PrefixoBaseFisica + Valor.ToLowerInvariant();

    /// <summary>Normaliza (apara e põe em maiúsculas) e valida; lança <see cref="EntradaInvalidaException"/> se inválido.</summary>
    public static NomeTenant Criar(string? nome)
    {
        var aparado = (nome ?? string.Empty).Trim();
        var normalizado = aparado.ToUpperInvariant();

        // ASCII ANTES de pôr em maiúsculas: 'ı' e 'ſ' virariam 'I' e 'S' e passariam calados pelo padrão.
        if (!System.Text.Ascii.IsValid(aparado) || !Padrao().IsMatch(normalizado))
        {
            throw new EntradaInvalidaException(
                $"Nome do tenant inválido: use de {TamanhoMinimo} a {TamanhoMaximo} caracteres entre letras sem acento, dígitos e _, " +
                "começando por letra (ex.: STAKE).");
        }

        return new NomeTenant(normalizado);
    }

    public override string ToString() => Valor;

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,39}$")]
    private static partial Regex Padrao();
}
