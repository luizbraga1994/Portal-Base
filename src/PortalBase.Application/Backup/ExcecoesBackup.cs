namespace PortalBase.Application.Backup;

/// <summary>
/// A infraestrutura de backup/restore FÍSICO (MySQL + ferramentas) está indisponível neste ambiente
/// (Prompt 50, Fases 33/34): a operação é BLOCKED, não um sucesso simulado. Fail-closed — nunca marca
/// backup/restore como concluído sem execução real. A mensagem não expõe host/credencial (regra 16).
/// </summary>
public sealed class ExecutorBackupIndisponivelException : InvalidOperationException
{
    public ExecutorBackupIndisponivelException()
        : base("Executor de backup/restore físico indisponível neste ambiente (MySQL/ferramentas ausentes).")
    {
    }

    public ExecutorBackupIndisponivelException(string message)
        : base(message)
    {
    }

    public ExecutorBackupIndisponivelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Tentativa de restaurar SOBRE a base de PRODUÇÃO (Prompt 50, Fases 14/15/28): o restore de teste
/// sempre aponta para uma base TEMPORÁRIA. Nunca sobrescreve produção. Fail-closed.
/// </summary>
public sealed class RestauracaoEmProducaoException : InvalidOperationException
{
    public RestauracaoEmProducaoException()
        : base("Restauração recusada: destino é a base de produção (o restore de teste exige base temporária).")
    {
    }

    public RestauracaoEmProducaoException(string message)
        : base(message)
    {
    }

    public RestauracaoEmProducaoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// O processo externo de backup/restore (<c>mysqldump</c>/<c>mysql</c>) terminou com erro (Prompt 50,
/// Fases 37/38). A mensagem carrega apenas o código de saída e um trecho SANITIZADO do stderr — NUNCA
/// senha, connection string ou o caminho do arquivo de credenciais (regra 16). Fail-closed.
/// </summary>
public sealed class ExecutorBackupProcessoException : InvalidOperationException
{
    public ExecutorBackupProcessoException(int exitCode, string erroSanitizado)
        : base($"Processo de backup/restore falhou (exit={exitCode}): {erroSanitizado}")
    {
    }
}

/// <summary>
/// Backup está em andamento durante uma MIGRATION (Prompt 50, Fase 8) e a política determina bloquear
/// para não gerar artefato inconsistente. Fail-closed.
/// </summary>
public sealed class BackupBloqueadoPorMigracaoException : InvalidOperationException
{
    public BackupBloqueadoPorMigracaoException()
        : base("Backup bloqueado: migration em andamento (evita artefato inconsistente).")
    {
    }

    public BackupBloqueadoPorMigracaoException(string message)
        : base(message)
    {
    }

    public BackupBloqueadoPorMigracaoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Já existe uma operação de backup/restauração em andamento para o mesmo alvo (bloqueio no banco). Nada foi feito;
/// tente de novo quando a operação atual terminar.
/// </summary>
public sealed class OperacaoBackupEmAndamentoException : InvalidOperationException
{
    public OperacaoBackupEmAndamentoException()
        : base("Já existe uma operação de backup ou restauração em andamento para este alvo.")
    {
    }

    public OperacaoBackupEmAndamentoException(string message)
        : base(message)
    {
    }

    public OperacaoBackupEmAndamentoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A restauração foi APLICADA (a base do contribuinte já tem os dados do backup e a anterior está preservada), mas a
/// atualização do schema (migrations) falhou: o contribuinte fica DESABILITADO (fail-closed) até a equipe técnica
/// aplicar as migrations. A mensagem não carrega detalhe técnico.
/// </summary>
public sealed class MigracaoAposRestauracaoException : InvalidOperationException
{
    public MigracaoAposRestauracaoException()
        : base("Restauração aplicada, mas a atualização do schema falhou; o contribuinte ficou desabilitado.")
    {
    }

    public MigracaoAposRestauracaoException(string message)
        : base(message)
    {
    }

    public MigracaoAposRestauracaoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
