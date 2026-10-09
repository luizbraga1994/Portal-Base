namespace PortalBase.Infrastructure.Opcoes;

/// <summary>
/// Configuração do STORE de artefatos de backup (Prompt 50, Fases 6, 25). O destino deve ficar em um
/// FAILURE DOMAIN separado do servidor de banco (Fase 25) e, idealmente, com uma cópia OFFSITE (Fase 21).
/// Nenhum segredo aqui. Em Produção o diretório raiz é obrigatório (fail-closed) — validado na DI.
/// </summary>
public sealed class OpcoesArmazenamentoBackup
{
    public const string Secao = "ArmazenamentoBackup";

    /// <summary>
    /// Diretório RAIZ do store de backups. DEVE ficar fora do datadir do banco e, em produção, em outro
    /// volume/host (failure domain distinto). Vazio em Dev/Test cai para um diretório temporário local.
    /// </summary>
    public string DiretorioRaiz { get; set; } = string.Empty;

    /// <summary>Diretório OFFSITE (cópia externa — Fase 21). Vazio ⇒ offsite BLOCKED (documentado).</summary>
    public string DiretorioOffsite { get; set; } = string.Empty;
}
