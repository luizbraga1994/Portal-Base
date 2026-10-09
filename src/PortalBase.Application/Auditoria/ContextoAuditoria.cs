namespace PortalBase.Application.Auditoria;

/// <summary>
/// Contexto de correlação com escopo de requisição/execução (seções 10-12). Começa vazio; o
/// host (Web/Worker) o preenche. O tipo de ator padrão é <see cref="TipoAtor.Usuario"/> —
/// execuções de background definem explicitamente <see cref="TipoAtor.JobBackground"/>.
/// </summary>
public sealed class ContextoAuditoria : IContextoAuditoriaMutavel
{
    public string? CorrelationId { get; private set; }

    public string? TraceId { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public string? Origem { get; private set; }

    public string? Job { get; private set; }

    public TipoAtor TipoAtor { get; private set; } = TipoAtor.Usuario;

    public void DefinirCorrelacao(string? correlationId, string? traceId)
    {
        CorrelationId = correlationId;
        TraceId = traceId;
    }

    public void DefinirOrigemHttp(string? ipAddress, string? userAgent)
    {
        IpAddress = ipAddress;
        UserAgent = userAgent;
    }

    public void DefinirOrigem(string origem, TipoAtor tipoAtor, string? job = null)
    {
        Origem = origem;
        TipoAtor = tipoAtor;
        Job = job;
    }
}
