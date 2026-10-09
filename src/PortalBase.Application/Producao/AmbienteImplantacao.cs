namespace PortalBase.Application.Producao;

/// <summary>
/// AMBIENTE DE IMPLANTAÇÃO (Prompt 33, seções 7/46) — separa claramente os ambientes operacionais.
/// É o ambiente de EXECUÇÃO da plataforma (não o de uma API externa). <see cref="ProducaoRestrita"/>
/// (homologação/piloto) é um ambiente SEPARADO de <see cref="Producao"/> — nunca confundir os dois (§46).
/// </summary>
public enum AmbienteImplantacao
{
    Development = 1,
    Test = 2,
    Staging = 3,

    /// <summary>Produção Restrita (empresas piloto) — ambiente produtivo separado da Produção plena (§46).</summary>
    ProducaoRestrita = 4,

    /// <summary>Produção plena.</summary>
    Producao = 5,
}

/// <summary>
/// Classifica o nome do ambiente (ex.: <c>ASPNETCORE_ENVIRONMENT</c>) em <see cref="AmbienteImplantacao"/>,
/// de forma explícita e sem ambiguidade (§7). Nome desconhecido/vazio NÃO é tratado como produtivo
/// (fail-safe: não aplica exigências de produção a um ambiente não reconhecido, e nunca confunde
/// Produção Restrita com Produção).
/// </summary>
public static class ClassificadorAmbiente
{
    /// <summary>Ambientes PRODUTIVOS (exigem configuração completa e segredos no cofre).</summary>
    public static bool EhProdutivo(AmbienteImplantacao ambiente)
        => ambiente is AmbienteImplantacao.ProducaoRestrita or AmbienteImplantacao.Producao;

    /// <summary>
    /// Tenta classificar o nome do ambiente. Retorna <c>false</c> (e <see cref="AmbienteImplantacao.Development"/>)
    /// para nomes desconhecidos/vazios — que NÃO são produtivos.
    /// </summary>
    public static bool TentarClassificar(string? nome, out AmbienteImplantacao ambiente)
    {
        switch ((nome ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "development":
            case "dev":
                ambiente = AmbienteImplantacao.Development;
                return true;
            case "test":
            case "testing":
            case "tests":
                ambiente = AmbienteImplantacao.Test;
                return true;
            case "staging":
            case "homolog":
            case "homologacao":
                ambiente = AmbienteImplantacao.Staging;
                return true;
            case "productionrestricted":
            case "producaorestrita":
            case "producao-restrita":
                ambiente = AmbienteImplantacao.ProducaoRestrita;
                return true;
            case "production":
            case "producao":
                ambiente = AmbienteImplantacao.Producao;
                return true;
            default:
                ambiente = AmbienteImplantacao.Development;
                return false;
        }
    }

    /// <summary>Classifica; nome desconhecido/vazio ⇒ <see cref="AmbienteImplantacao.Development"/> (não produtivo).</summary>
    public static AmbienteImplantacao Classificar(string? nome)
        => TentarClassificar(nome, out var ambiente) ? ambiente : AmbienteImplantacao.Development;
}
