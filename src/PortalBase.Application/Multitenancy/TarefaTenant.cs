using PortalBase.Application.Auditoria;

namespace PortalBase.Application.Multitenancy;

/// <summary>Unidade de trabalho do Worker executada SEMPRE sob um tenant explícito (seção 23).</summary>
public interface ITarefaTenant
{
    /// <summary>Nome do job (para correlação/auditoria — seção 10).</summary>
    string Nome { get; }

    Task ExecutarAsync(ITenantContext contexto, CancellationToken cancellationToken);
}

/// <summary>
/// Executa uma <see cref="ITarefaTenant"/> estabelecendo, de forma independente por
/// operação, o contexto do tenant (resolução + autorização). Sem tenant autorizado a
/// tarefa não roda (fail-closed). Nunca reaproveita o "último tenant". Estabelece o contexto
/// de auditoria da execução (ator = JobBackground, nome do job, correlation id) — seção 10.
/// </summary>
public interface IExecutorTarefaTenant
{
    Task ExecutarAsync(string principalSistema, Guid tenantId, ITarefaTenant tarefa, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IExecutorTarefaTenant"/>
public sealed class ExecutorTarefaTenant : IExecutorTarefaTenant
{
    private readonly IResolvedorTenant _resolvedor;
    private readonly IContextoAuditoriaMutavel _auditoria;

    public ExecutorTarefaTenant(IResolvedorTenant resolvedor, IContextoAuditoriaMutavel auditoria)
    {
        ArgumentNullException.ThrowIfNull(resolvedor);
        ArgumentNullException.ThrowIfNull(auditoria);
        _resolvedor = resolvedor;
        _auditoria = auditoria;
    }

    public async Task ExecutarAsync(
        string principalSistema,
        Guid tenantId,
        ITarefaTenant tarefa,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tarefa);

        // Contexto de auditoria do job: ator explícito de background, nome do job e correlação
        // (o tenant é resolvido e validado a seguir — nunca assume tenant arbitrário, seção 10).
        _auditoria.DefinirOrigem("Worker", TipoAtor.JobBackground, tarefa.Nome);
        _auditoria.DefinirCorrelacao(Guid.NewGuid().ToString("N"), null);

        // Lança se não autorizado — a tarefa nunca executa sem tenant válido (deny-by-default).
        var contexto = await _resolvedor
            .ResolverAsync(principalSistema, tenantId, cancellationToken)
            .ConfigureAwait(false);

        await tarefa.ExecutarAsync(contexto, cancellationToken).ConfigureAwait(false);
    }
}
