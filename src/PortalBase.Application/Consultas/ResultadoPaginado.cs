namespace PortalBase.Application.Consultas;

/// <summary>
/// Resultado de leitura PAGINADO e genérico (Prompt 36, seção 3). É a projeção padrão de todos os
/// read-models: nunca carrega a tabela inteira, nunca expõe entidade EF nem agregado de domínio —
/// apenas <typeparamref name="T"/> (um DTO de leitura). Imutável; construção valida invariantes.
/// </summary>
/// <typeparam name="T">DTO de leitura (projeção) — NUNCA uma entidade EF ou agregado de domínio.</typeparam>
public sealed record ResultadoPaginado<T>
{
    /// <summary>Itens da página atual (projeções). Lista vazia quando não há resultados.</summary>
    public IReadOnlyList<T> Itens { get; }

    /// <summary>Página atual (base 1).</summary>
    public int Pagina { get; }

    /// <summary>Tamanho de página efetivamente aplicado (já normalizado/limitado).</summary>
    public int TamanhoPagina { get; }

    /// <summary>Total de itens que satisfazem o filtro (sobre TODAS as páginas), não só a atual.</summary>
    public long TotalItens { get; }

    /// <summary>Total de páginas para o <see cref="TamanhoPagina"/> atual.</summary>
    public int TotalPaginas { get; }

    /// <summary>Existe página anterior?</summary>
    public bool TemAnterior => Pagina > 1;

    /// <summary>Existe próxima página?</summary>
    public bool TemProxima => Pagina < TotalPaginas;

    /// <summary>Cria um resultado paginado a partir de uma projeção já materializada e do total.</summary>
    public ResultadoPaginado(IReadOnlyList<T> itens, int pagina, int tamanhoPagina, long totalItens)
    {
        ArgumentNullException.ThrowIfNull(itens);
        ArgumentOutOfRangeException.ThrowIfLessThan(pagina, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tamanhoPagina, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(totalItens);

        Itens = itens;
        Pagina = pagina;
        TamanhoPagina = tamanhoPagina;
        TotalItens = totalItens;
        TotalPaginas = (int)((totalItens + tamanhoPagina - 1) / tamanhoPagina);
    }
}

/// <summary>Fábricas não-genéricas para <see cref="ResultadoPaginado{T}"/> (evita membro estático em tipo genérico — CA1000).</summary>
public static class ResultadoPaginado
{
    /// <summary>Página vazia (nenhum resultado) preservando a paginação solicitada.</summary>
    public static ResultadoPaginado<T> Vazio<T>(int pagina, int tamanhoPagina) =>
        new(Array.Empty<T>(), pagina, tamanhoPagina, 0);
}
