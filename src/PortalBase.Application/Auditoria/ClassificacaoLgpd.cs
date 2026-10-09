namespace PortalBase.Application.Auditoria;

/// <summary>
/// Classe de eliminabilidade LGPD de uma categoria de retenção (Prompt 57, Fases 15/40).
/// Distingue, de forma legível por máquina, o que NÃO pode ser eliminado por pedido do titular
/// enquanto há obrigação legal do que seria elegível a expurgo/anonimização após a janela.
/// </summary>
public enum ClasseLgpdRetencao
{
    /// <summary>
    /// Retido por OBRIGAÇÃO LEGAL (auditoria, arquivos de importação — acrescente as categorias do seu domínio). Preservado enquanto exigido — NUNCA expurgável por pedido do titular
    /// durante a janela (a base legal do tratamento é cumprimento de obrigação legal/regulatória).
    /// </summary>
    RetencaoLegalObrigatoria = 1,

    /// <summary>
    /// Dado expirável/pessoal SEM obrigação legal remanescente após a janela: elegível a
    /// expurgo/anonimização quando a janela terminar. Nenhuma categoria atual do Portal Base é
    /// desta classe — os dados hoje mapeados são todos de retenção legal obrigatória.
    /// </summary>
    ElegivelExpurgoAposJanela = 2,
}

/// <summary>
/// Classificação LGPD sobre a política de retenção existente (Prompt 57). É um <b>guarda</b>: NÃO
/// apaga, anonimiza nem exporta nada — apenas responde se um dado, dada sua categoria e janela,
/// PODERIA ser expurgado. Qualquer mecanismo futuro de expurgo/anonimização DEVE consultar este
/// guarda antes de tocar em qualquer registro, garantindo que nenhum dado de retenção obrigatória seja
/// removido sem regra. O padrão conservador (categoria desconhecida) é <b>preservar</b>.
/// </summary>
public static class ClassificacaoLgpd
{
    /// <summary>Classe de eliminabilidade de uma categoria de retenção.</summary>
    public static ClasseLgpdRetencao Classe(CategoriaRetencao categoria) => categoria switch
    {
        CategoriaRetencao.Auditoria => ClasseLgpdRetencao.RetencaoLegalObrigatoria,
        CategoriaRetencao.ArquivosImportacao => ClasseLgpdRetencao.RetencaoLegalObrigatoria,
        _ => ClasseLgpdRetencao.RetencaoLegalObrigatoria, // conservador: desconhecido = preservar
    };

    /// <summary>
    /// Núcleo puro da decisão: só é expurgável quem NÃO tem obrigação legal remanescente E cuja
    /// janela de retenção já terminou. Dado de retenção legal obrigatória nunca é expurgável aqui.
    /// </summary>
    public static bool PodeSerExpurgado(ClasseLgpdRetencao classe, bool dentroDaJanela) =>
        classe == ClasseLgpdRetencao.ElegivelExpurgoAposJanela && !dentroDaJanela;

    /// <summary>
    /// Decide por categoria + política (conveniência). NÃO executa expurgo — apenas classifica.
    /// Retorna <c>true</c> somente se a categoria for elegível E a janela de retenção houver terminado.
    /// </summary>
    public static bool PodeSerExpurgado(
        IPoliticaRetencao politica,
        CategoriaRetencao categoria,
        DateTimeOffset ocorridoEmUtc,
        DateTimeOffset agoraUtc)
    {
        ArgumentNullException.ThrowIfNull(politica);
        var dentro = politica.DentroDaJanela(categoria, ocorridoEmUtc, agoraUtc);
        return PodeSerExpurgado(Classe(categoria), dentro);
    }
}
