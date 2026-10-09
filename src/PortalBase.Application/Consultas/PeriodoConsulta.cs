using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Consultas;

/// <summary>
/// Regra ÚNICA de PERÍODO (intervalo <c>Desde</c>/<c>Até</c>) das listagens temporais (Prompt 81). Usada pela
/// UI (pré-validação do <c>FiltroPeriodo</c>, que bloqueia a consulta) e, OBRIGATORIAMENTE, pelos serviços de
/// consulta autorizados no servidor — a validação da interface nunca é a única barreira (regras 3 e 15).
/// Ambos os limites são INCLUSIVOS; qualquer um pode faltar (sem limite daquele lado). As datas digitadas
/// são dias UTC, coerentes com os carimbos <c>*EmUtc</c> exibidos nas listas (nenhum fuso é inventado).
/// </summary>
public static class PeriodoConsulta
{
    /// <summary>Mensagem segura (sem detalhe interno) de período inválido.</summary>
    public const string MensagemPeriodoInvalido = "Período inválido: a data inicial deve ser anterior ou igual à data final.";

    // Último instante representável por datetime(6) (microssegundo) dentro de um dia.
    private static readonly TimeOnly UltimoMicrossegundo = new(23, 59, 59, 999, 999);

    /// <summary><c>true</c> quando o intervalo é aceitável: algum limite ausente ou <c>Desde &lt;= Até</c>.</summary>
    public static bool EhValido(DateTimeOffset? desde, DateTimeOffset? ate)
        => desde is not { } inicio || ate is not { } fim || inicio <= fim;

    /// <summary>Fail-closed: <c>Desde &gt; Até</c> lança <see cref="EntradaInvalidaException"/> ANTES de qualquer consulta.</summary>
    public static void Validar(DateTimeOffset? desde, DateTimeOffset? ate)
    {
        if (!EhValido(desde, ate))
        {
            throw new EntradaInvalidaException(MensagemPeriodoInvalido);
        }
    }

    /// <summary>Primeiro instante (00:00:00.0000000 UTC) do dia informado — limite inclusivo de <c>Desde</c>.</summary>
    public static DateTimeOffset InicioDoDia(DateOnly data) => new(data.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// Último instante (23:59:59.999999 UTC) do dia informado — limite inclusivo de <c>Até</c>, na MESMA precisão
    /// (microssegundo) das colunas <c>datetime(6)</c>: um registro às 23:59:59.5 pertence ao dia e nenhum
    /// arredondamento do parâmetro pode alcançar 00:00:00 do dia seguinte.
    /// </summary>
    public static DateTimeOffset FimDoDia(DateOnly data) => new(data.ToDateTime(UltimoMicrossegundo), TimeSpan.Zero);
}
