using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Auditoria;

/// <summary>Resultado de uma decisão auditada.</summary>
public enum ResultadoAuditoria
{
    Permitido,
    Negado,
}

/// <summary>
/// Evento de auditoria de uma operação crítica (regra 9; Prompt 05). Campos obrigatórios
/// (quem/tenant/ação/resultado/quando) mais campos OPCIONAIS de rastreabilidade (recurso,
/// motivo, competência, protocolo, valores antes/depois, metadados, categoria de retenção).
/// A correlação (correlation/trace/IP/user-agent/source/ator) é preenchida pelo <b>sink</b> a
/// partir do <see cref="IContextoAuditoria"/> ambiente — os chamadores não a repetem.
/// NUNCA contém segredos, tokens, senhas nem dados sensíveis (regras 8; seções 6, 7).
/// </summary>
public sealed record EventoAuditoria
{
    public EventoAuditoria(
        string principalId,
        string tenant,
        string operacao,
        ResultadoAuditoria resultado,
        DateTimeOffset ocorridoEm)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            throw new EntradaInvalidaException("PrincipalId do evento de auditoria é obrigatório.");
        }

        if (string.IsNullOrWhiteSpace(tenant))
        {
            throw new EntradaInvalidaException("Tenant do evento de auditoria é obrigatório.");
        }

        if (string.IsNullOrWhiteSpace(operacao))
        {
            throw new EntradaInvalidaException("Operação do evento de auditoria é obrigatória.");
        }

        PrincipalId = principalId;
        Tenant = tenant;
        Operacao = operacao;
        Resultado = resultado;
        OcorridoEm = ocorridoEm;
    }

    public string PrincipalId { get; }

    public string Tenant { get; }

    /// <summary>Ação/evento (ver <see cref="AcaoAuditoria"/>).</summary>
    public string Operacao { get; }

    public ResultadoAuditoria Resultado { get; }

    public DateTimeOffset OcorridoEm { get; }

    // ---- Enriquecimento opcional (rastreabilidade) ----

    /// <summary>Tipo do recurso afetado (ex.: "Usuario", "Tenant", "CodTrib").</summary>
    public string? TipoRecurso { get; init; }

    /// <summary>Identificador do recurso afetado.</summary>
    public string? RecursoId { get; init; }

    /// <summary>Motivo/observação sanitizada (nunca segredo).</summary>
    public string? Motivo { get; init; }

    /// <summary>Competência (AAAA-MM) quando aplicável.</summary>
    public string? Competencia { get; init; }

    /// <summary>Protocolo externo (ex.: de uma API) quando aplicável.</summary>
    public string? Protocolo { get; init; }

    /// <summary>Categoria de retenção. Padrão: trilha de auditoria (10 anos).</summary>
    public CategoriaRetencao Categoria { get; init; } = CategoriaRetencao.Auditoria;

    /// <summary>Valores anteriores (allowlist de propriedades — nunca o objeto inteiro; seção 6).</summary>
    public IReadOnlyDictionary<string, string?>? ValoresAnteriores { get; init; }

    /// <summary>Valores novos (allowlist de propriedades — nunca o objeto inteiro; seção 6).</summary>
    public IReadOnlyDictionary<string, string?>? ValoresNovos { get; init; }

    /// <summary>Metadados adicionais (serão sanitizados na persistência; seção 13).</summary>
    public IReadOnlyDictionary<string, string?>? Metadados { get; init; }

    public static EventoAuditoria Permitido(string principalId, string tenant, string operacao, DateTimeOffset ocorridoEm)
        => new(principalId, tenant, operacao, ResultadoAuditoria.Permitido, ocorridoEm);

    public static EventoAuditoria Negado(string principalId, string tenant, string operacao, DateTimeOffset ocorridoEm)
        => new(principalId, tenant, operacao, ResultadoAuditoria.Negado, ocorridoEm);
}

/// <summary>
/// Sink da TRILHA DE AUDITORIA (regra 9; seção 8). Persiste de forma durável, append-only e
/// enriquecida com o contexto de correlação. A camada Application não conhece EF/MySQL/tabela.
/// </summary>
public interface IAuditoria
{
    ValueTask RegistrarAsync(EventoAuditoria evento, CancellationToken cancellationToken = default);
}

/// <summary>
/// Contexto de correlação AMBIENTE de uma operação (seções 10, 11, 12), escopo de
/// requisição/execução. Alimentado pela apresentação (HTTP/Blazor) ou pelo host de background;
/// o sink de auditoria e o logging estruturado o consomem. Não confiar em identificador de
/// tenant vindo do cliente — o tenant efetivo é o resolvido no servidor.
/// </summary>
public interface IContextoAuditoria
{
    string? CorrelationId { get; }

    string? TraceId { get; }

    string? IpAddress { get; }

    string? UserAgent { get; }

    /// <summary>Origem lógica (ex.: "Web", "Worker").</summary>
    string? Origem { get; }

    /// <summary>Nome do job, quando execução de background.</summary>
    string? Job { get; }

    TipoAtor TipoAtor { get; }
}

/// <summary>Face mutável do contexto de correlação, escrita apenas pelo host/apresentação.</summary>
public interface IContextoAuditoriaMutavel : IContextoAuditoria
{
    void DefinirCorrelacao(string? correlationId, string? traceId);

    void DefinirOrigemHttp(string? ipAddress, string? userAgent);

    void DefinirOrigem(string origem, TipoAtor tipoAtor, string? job = null);
}
