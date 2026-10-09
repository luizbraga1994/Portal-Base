namespace PortalBase.Infrastructure.Persistencia.Catalogo;

/// <summary>
/// Registro persistente da trilha de auditoria do CATÁLOGO (regra 9; Prompt 05). Append-only:
/// a aplicação apenas INSERE — não há caminho de UPDATE/DELETE. Não contém segredos, senhas,
/// tokens, connection strings nem dados contábeis (regras 8; seções 6, 7). O
/// <see cref="IntegridadeHash"/> torna a adulteração de qualquer campo detectável (seção 14).
/// </summary>
public sealed class RegistroAuditoria
{
    public long Id { get; set; }

    public required string Tenant { get; set; }

    public required string PrincipalId { get; set; }

    /// <summary>Tipo do ator (1=Usuario, 2=Sistema, 3=JobBackground, 4=Integracao).</summary>
    public int TipoAtor { get; set; }

    /// <summary>Ação/evento (ver AcaoAuditoria).</summary>
    public required string Operacao { get; set; }

    public string? TipoRecurso { get; set; }

    public string? RecursoId { get; set; }

    public required string Resultado { get; set; }

    public string? Motivo { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public string? Origem { get; set; }

    public string? Job { get; set; }

    public string? Competencia { get; set; }

    public string? Protocolo { get; set; }

    /// <summary>Categoria de retenção (1=Auditoria/10a, ...). Ver CategoriaRetencao.</summary>
    public int Categoria { get; set; }

    /// <summary>Valores anteriores sanitizados (JSON). Nunca o objeto inteiro (seção 6).</summary>
    public string? ValoresAnteriores { get; set; }

    /// <summary>Valores novos sanitizados (JSON).</summary>
    public string? ValoresNovos { get; set; }

    /// <summary>Metadados sanitizados (JSON).</summary>
    public string? Metadados { get; set; }

    public DateTime OcorridoEmUtc { get; set; }

    /// <summary>HMAC de integridade sobre os campos canônicos (tamper-evidence — seção 14).</summary>
    public required string IntegridadeHash { get; set; }
}
