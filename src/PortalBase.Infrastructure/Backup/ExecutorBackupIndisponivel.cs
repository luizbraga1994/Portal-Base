using PortalBase.Application.Backup;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// Implementação PADRÃO do executor físico de backup/restore quando NÃO há MySQL + ferramentas
/// (<c>mysqldump</c>/<c>mysql</c>) disponíveis neste ambiente (Prompt 50, Fases 33/34). <see cref="Disponivel"/>
/// é <c>false</c>: toda tentativa de dump/restore físico é BLOCKED (fail-closed) — NUNCA simulada como
/// sucesso. Em produção, um executor real baseado em processo (mysqldump/mysql, credenciais do cofre via
/// arquivo de opções seguro, base temporária no restore) substitui esta implementação atrás do mesmo porto.
/// </summary>
public sealed class ExecutorBackupIndisponivel : IExecutorBackupBanco
{
    public bool Disponivel => false;

    public Task GerarDumpAsync(string nomeBaseLogica, Stream destino, CancellationToken cancellationToken = default)
        => throw new ExecutorBackupIndisponivelException();

    public Task RestaurarEmBaseTemporariaAsync(string nomeBaseTemporaria, Stream origem, CancellationToken cancellationToken = default)
        => throw new ExecutorBackupIndisponivelException();
}
