namespace PortalBase.Application.Auditoria;

/// <summary>
/// Política de RETENÇÃO (seção 18). Cada categoria tem um prazo legal mínimo; a política
/// calcula a janela e diz se um registro ainda está dentro dela. Retenção NÃO implica
/// exclusão automática ao expirar — apenas identifica a categoria e o fim da obrigação legal
/// (seções 18, 24). Datas sempre em UTC.
/// </summary>
public interface IPoliticaRetencao
{
    /// <summary>Prazo (anos) da categoria.</summary>
    int AnosDe(CategoriaRetencao categoria);

    /// <summary>Data (UTC) em que a obrigação de retenção termina para um registro.</summary>
    DateTimeOffset ExpiraEm(CategoriaRetencao categoria, DateTimeOffset ocorridoEmUtc);

    /// <summary>O registro ainda está dentro da janela obrigatória em <paramref name="agoraUtc"/>?</summary>
    bool DentroDaJanela(CategoriaRetencao categoria, DateTimeOffset ocorridoEmUtc, DateTimeOffset agoraUtc);
}

/// <summary>
/// Prazos de retenção (ajuste às obrigações legais do domínio do seu portal):
/// arquivos de importação 5 anos · trilha de auditoria 10 anos.
/// </summary>
public sealed class PoliticaRetencao : IPoliticaRetencao
{
    public int AnosDe(CategoriaRetencao categoria) => categoria switch
    {
        CategoriaRetencao.Auditoria => 10,
        CategoriaRetencao.ArquivosImportacao => 5,
        _ => 10, // conservador: o maior prazo em caso de categoria desconhecida
    };

    public DateTimeOffset ExpiraEm(CategoriaRetencao categoria, DateTimeOffset ocorridoEmUtc)
        => ocorridoEmUtc.ToUniversalTime().AddYears(AnosDe(categoria));

    public bool DentroDaJanela(CategoriaRetencao categoria, DateTimeOffset ocorridoEmUtc, DateTimeOffset agoraUtc)
        => agoraUtc.ToUniversalTime() < ExpiraEm(categoria, ocorridoEmUtc);
}
