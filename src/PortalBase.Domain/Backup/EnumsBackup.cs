namespace PortalBase.Domain.Backup;

/// <summary>
/// Tipo do alvo de um backup (Prompt 50). Respeita a separação CATALOG DB + DATABASE-PER-TENANT:
/// o backup do catálogo é distinto do backup de cada base de contribuinte, e o storage (arquivos
/// fiscais) é um alvo próprio. NUNCA existe um tipo "segredos": segredos ficam no cofre e só a
/// REFERÊNCIA é preservada (regra 7).
/// </summary>
public enum TipoBackup
{
    /// <summary>Backup completo do banco de CATÁLOGO (tenants, usuários, papéis, auditoria, metadados).</summary>
    CatalogoCompleto = 1,

    /// <summary>Backup completo da base física de UM tenant (dados do contribuinte).</summary>
    TenantCompleto = 2,

    /// <summary>Backup do STORAGE privado (arquivos fiscais por tenant + metadados) — nunca segredos.</summary>
    Storage = 3,
}

/// <summary>
/// Situação da CÓPIA EXTERNA (offsite) de um artefato concluído (BKP-003, auditoria de 08/10/2026): a cópia é gravada em
/// outro destino (outro disco ou servidor) e conferida pelo hash SHA-256. Nulo no registro = cópia externa não
/// configurada no servidor.
/// </summary>
public enum SituacaoCopiaExterna
{
    /// <summary>Cópia gravada no destino externo e conferida (mesmo hash do artefato).</summary>
    Concluida = 1,

    /// <summary>A cópia não pôde ser gravada ou não conferiu: o artefato só existe no destino primário.</summary>
    Falhou = 2,
}

/// <summary>
/// Estado do ciclo de vida de um registro de backup (Prompt 50, Fase 13). Um artefato só é
/// CONFIÁVEL quando <see cref="Concluido"/> COM hash de integridade; <see cref="Corrompido"/>
/// marca um artefato cuja verificação de integridade falhou (nunca deve ser restaurado).
/// </summary>
public enum StatusBackup
{
    /// <summary>Iniciado; artefato ainda não finalizado (não restaurável).</summary>
    Iniciado = 1,

    /// <summary>Concluído com hash de integridade calculado — candidato a restauração.</summary>
    Concluido = 2,

    /// <summary>Falhou durante a geração/gravação — não restaurável.</summary>
    Falhou = 3,

    /// <summary>Verificação de integridade detectou adulteração/corrupção — NUNCA restaurar.</summary>
    Corrompido = 4,

    /// <summary>Expirado pela política de retenção (fora da janela) — elegível a descarte.</summary>
    Expirado = 5,
}
