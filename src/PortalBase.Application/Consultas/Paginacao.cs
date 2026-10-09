namespace PortalBase.Application.Consultas;

/// <summary>Direção de ordenação — parte da whitelist de ordenação (nunca <c>ORDER BY</c> arbitrário do cliente).</summary>
public enum DirecaoOrdenacao
{
    /// <summary>Crescente.</summary>
    Ascendente = 0,

    /// <summary>Decrescente (padrão típico das listagens temporais: mais recentes primeiro).</summary>
    Descendente = 1,
}

/// <summary>
/// Limites e normalização de PAGINAÇÃO server-side, compartilhados por todos os read-models
/// (Prompt 36, seções 3-4). Impede <c>pageSize</c> ilimitado, página &lt; 1 e materialização
/// da tabela inteira. Os filtros tipados de cada área normalizam sua paginação com estes limites.
/// </summary>
public static class Paginacao
{
    /// <summary>Menor tamanho de página aceito.</summary>
    public const int TamanhoMinimo = 1;

    /// <summary>Tamanho de página padrão quando o solicitado é inválido/ausente.</summary>
    public const int TamanhoPadrao = 50;

    /// <summary>Maior tamanho de página aceito (teto de segurança — nunca <c>pageSize = 1000000</c>).</summary>
    public const int TamanhoMaximo = 200;

    /// <summary>Normaliza a página para a base 1 (valores &lt; 1 viram 1).</summary>
    public static int NormalizarPagina(int pagina) => pagina < 1 ? 1 : pagina;

    /// <summary>Normaliza o tamanho da página para a faixa segura [<see cref="TamanhoMinimo"/>, <see cref="TamanhoMaximo"/>].</summary>
    public static int NormalizarTamanho(int tamanho) =>
        tamanho < TamanhoMinimo ? TamanhoPadrao
        : tamanho > TamanhoMaximo ? TamanhoMaximo
        : tamanho;

    /// <summary>Quantidade de registros a saltar (<c>Skip</c>) para a página/tamanho informados (já normalizados).</summary>
    public static int Saltar(int pagina, int tamanho) => (NormalizarPagina(pagina) - 1) * NormalizarTamanho(tamanho);
}
