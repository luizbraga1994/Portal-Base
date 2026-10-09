using System.Globalization;
using PortalBase.Application.Consultas;

namespace PortalBase.Web.Ui.Dados;

/// <summary>
/// Conversão texto ⇄ limite de período do <c>FiltroPeriodo</c> (Prompt 81). O valor vem do
/// <c>&lt;input type="date"&gt;</c> (<c>yyyy-MM-dd</c>, dia UTC); vazio/malformado ⇒ <c>null</c> (sem limite),
/// nunca exceção. Os limites de dia vêm da regra única <see cref="PeriodoConsulta"/> (não há segunda regra).
/// </summary>
public static class PeriodoUi
{
    private const string FormatoData = "yyyy-MM-dd";

    /// <summary>Delegação à regra única do servidor: a página NÃO consulta enquanto o período for inválido.</summary>
    public static bool EhValido(DateTimeOffset? desde, DateTimeOffset? ate) => PeriodoConsulta.EhValido(desde, ate);

    /// <summary>Interpreta a data digitada como início (<c>Desde</c>) ou fim INCLUSIVO (<c>Até</c>) do dia UTC.</summary>
    public static DateTimeOffset? Interpretar(string? valor, bool fimDoDia)
    {
        if (string.IsNullOrWhiteSpace(valor)
            || !DateOnly.TryParseExact(valor.Trim(), FormatoData, CultureInfo.InvariantCulture, DateTimeStyles.None, out var data))
        {
            return null;
        }

        return fimDoDia ? PeriodoConsulta.FimDoDia(data) : PeriodoConsulta.InicioDoDia(data);
    }

    /// <summary>Valor do <c>&lt;input type="date"&gt;</c> para o limite (dia UTC); <c>null</c> ⇒ campo vazio.</summary>
    public static string Formatar(DateTimeOffset? valor)
        => valor is { } v ? v.UtcDateTime.ToString(FormatoData, CultureInfo.InvariantCulture) : string.Empty;
}
