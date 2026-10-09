namespace PortalBase.Application.Auditoria;

/// <summary>
/// Projeção de leitura de um registro de auditoria (sem campos internos de integridade). <see cref="NomeAtor"/> e
/// <see cref="LoginAtor"/> são só para exibição — preenchidos no servidor a partir do catálogo quando o ator é um usuário;
/// o identificador gravado (<see cref="PrincipalId"/>) continua sendo a referência da trilha.
/// </summary>
public sealed record RegistroAuditoriaDto(
    long Id,
    string Tenant,
    string PrincipalId,
    TipoAtor TipoAtor,
    string Acao,
    string? TipoRecurso,
    string? RecursoId,
    string Resultado,
    string? Motivo,
    string? CorrelationId,
    string? TraceId,
    string? Protocolo,
    string? Competencia,
    DateTimeOffset OcorridoEmUtc,
    string? NomeAtor = null,
    string? LoginAtor = null);

/// <summary>
/// Filtro de consulta da trilha (seção 16). Paginação OBRIGATÓRIA e limitada. O
/// <see cref="Tenant"/> é definido pelo servidor a partir do contexto autorizado — NUNCA por
/// parâmetro do cliente (seções 16, 17, 20).
/// </summary>
public sealed record FiltroAuditoria
{
    public const int TamanhoMaximoPagina = 200;
    public const int TamanhoPadraoPagina = 50;

    /// <summary>Tenant da consulta — preenchido pelo servidor (não confiar no cliente).</summary>
    public required string Tenant { get; init; }

    public DateTimeOffset? Desde { get; init; }

    public DateTimeOffset? Ate { get; init; }

    /// <summary>Texto do filtro "Ator": identificador, nome, login ou e-mail.</summary>
    public string? Ator { get; init; }

    /// <summary>
    /// Identificadores de ator que casam com <see cref="Ator"/> — preenchido SÓ pelo servidor (o do cliente é descartado),
    /// a partir do catálogo de usuários. Vazio/nulo: o filtro compara <see cref="Ator"/> com o identificador gravado.
    /// </summary>
    public IReadOnlyList<string>? Atores { get; init; }

    public string? Acao { get; init; }

    public string? Resultado { get; init; }

    public string? TipoRecurso { get; init; }

    public string? RecursoId { get; init; }

    public string? CorrelationId { get; init; }

    public string? Protocolo { get; init; }

    public string? Competencia { get; init; }

    public int Pagina { get; init; } = 1;

    public int TamanhoPagina { get; init; } = TamanhoPadraoPagina;

    /// <summary>Ordenação temporal descendente por padrão (mais recentes primeiro).</summary>
    public bool DescendentePorData { get; init; } = true;

    /// <summary>Normaliza paginação para limites seguros (página &gt;= 1, tamanho 1..máx).</summary>
    public FiltroAuditoria Normalizado() => this with
    {
        Pagina = Pagina < 1 ? 1 : Pagina,
        TamanhoPagina = TamanhoPagina < 1 ? TamanhoPadraoPagina
            : TamanhoPagina > TamanhoMaximoPagina ? TamanhoMaximoPagina
            : TamanhoPagina,
    };
}

/// <summary>Página de resultados da consulta de auditoria.</summary>
public sealed record PaginaAuditoria(IReadOnlyList<RegistroAuditoriaDto> Itens, int Pagina, int TamanhoPagina, long Total);

/// <summary>Identificação de exibição de um usuário que aparece na trilha.</summary>
public sealed record IdentificacaoAtor(string Nome, string Login);

/// <summary>
/// Nomes de exibição dos atores da trilha, a partir do catálogo de usuários (Infrastructure implementa). Só traduz
/// identificadores que JÁ vieram da consulta autorizada — e o texto do filtro em identificadores; não amplia o que a
/// trilha mostra: o isolamento continua no filtro de tenant da consulta.
/// </summary>
public interface IConsultaNomesAtores
{
    /// <summary>Nome e login dos usuários com estes identificadores (os que não forem usuário ficam de fora).</summary>
    Task<IReadOnlyDictionary<string, IdentificacaoAtor>> IdentificarAsync(IReadOnlyCollection<string> principalIds, CancellationToken cancellationToken = default);

    /// <summary>Identificadores dos usuários cujo nome, login ou e-mail contém o texto (no máximo <paramref name="maximo"/>).</summary>
    Task<IReadOnlyList<string>> IdsPorTextoAsync(string texto, int maximo, CancellationToken cancellationToken = default);
}

/// <summary>
/// Porta de consulta da trilha (Infrastructure implementa sobre o catálogo). SEMPRE filtra por
/// tenant e pagina; nunca carrega a tabela inteira. Consultas parametrizadas (regra 13).
/// </summary>
public interface IConsultaAuditoria
{
    Task<PaginaAuditoria> ConsultarAsync(FiltroAuditoria filtro, CancellationToken cancellationToken = default);
}
