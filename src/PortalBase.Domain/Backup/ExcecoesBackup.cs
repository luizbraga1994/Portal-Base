using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Backup;

/// <summary>
/// Integridade do backup FALHOU (Prompt 50, Fase 26): o hash recalculado diverge do registrado,
/// ou o artefato foi adulterado. NUNCA restaurar depois disto. A mensagem não expõe caminho/segredo.
/// </summary>
public sealed class BackupCorrompidoException : DominioException
{
    public BackupCorrompidoException()
        : base("Integridade do backup inválida (hash divergente) — restauração recusada.")
    {
    }

    public BackupCorrompidoException(string message)
        : base(message)
    {
    }

    public BackupCorrompidoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Backup INVÁLIDO/incompleto (Prompt 50, Fase 27): artefato truncado, checksum ausente, metadados
/// incompatíveis ou registro não concluído. Rejeitado antes de qualquer tentativa de restauração.
/// </summary>
public sealed class BackupInvalidoException : DominioException
{
    public BackupInvalidoException()
        : base("Backup inválido ou incompleto.")
    {
    }

    public BackupInvalidoException(string message)
        : base(message)
    {
    }

    public BackupInvalidoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Tentativa de restaurar um backup em um ALVO de tenant diferente (Prompt 50, Fase 29): backup de A
/// só restaura em A. Metadados incompatíveis ⇒ fail-closed. Impede cruzamento de dados entre tenants.
/// </summary>
public sealed class RestauracaoCrossTenantException : DominioException
{
    public RestauracaoCrossTenantException()
        : base("Restauração cross-tenant recusada: o backup pertence a outro tenant.")
    {
    }

    public RestauracaoCrossTenantException(string message)
        : base(message)
    {
    }

    public RestauracaoCrossTenantException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
