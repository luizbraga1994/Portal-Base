using PortalBase.Application.Auditoria;

namespace PortalBase.Web.Ui.Dados;

/// <summary>Estado de uma consulta de auditoria para a UI.</summary>
public enum EstadoConsultaAuditoria
{
    Ok,
    Demonstracao,
    SemTenant,
    SemPermissao,
    Erro,
}

/// <summary>Filtro da tela de auditoria (§36). O tenant NUNCA é parâmetro do cliente — é do servidor.</summary>
public sealed record FiltroAuditoriaVm
{
    public DateTimeOffset? Desde { get; init; }
    public DateTimeOffset? Ate { get; init; }
    public string? Ator { get; init; }
    public string? Acao { get; init; }
    public string? Resultado { get; init; }
    public string? TipoRecurso { get; init; }
    public string? CorrelationId { get; init; }
    public int Pagina { get; init; } = 1;
    public int TamanhoPagina { get; init; } = 25;
}

/// <summary>Um registro de auditoria projetado para a UI (nunca campos internos/segredos).</summary>
public sealed record RegistroAuditoriaVm(
    long Id,
    string Ator,
    TipoAtor TipoAtor,
    string Acao,
    string? Recurso,
    string Resultado,
    string? Motivo,
    string? CorrelationId,
    string? Protocolo,
    string? Competencia,
    DateTimeOffset OcorridoEmUtc,
    string? NomeAtor = null,
    string? LoginAtor = null)
{
    public bool Permitido => string.Equals(Resultado, "Permitido", StringComparison.OrdinalIgnoreCase);

    /// <summary>O que a tela mostra como ator: o nome do usuário (do catálogo) ou, se não houver, o identificador gravado.</summary>
    public string AtorExibido => string.IsNullOrWhiteSpace(NomeAtor) ? Ator : NomeAtor;

    /// <summary>Linha de apoio do ator: o tipo e, para usuários, o login.</summary>
    public string DescricaoAtor
    {
        get
        {
            var tipo = TipoAtor switch
            {
                TipoAtor.Usuario => "Usuário",
                TipoAtor.Sistema => "Sistema",
                TipoAtor.JobBackground => "Rotina automática",
                TipoAtor.Integracao => "Integração",
                _ => TipoAtor.ToString(),
            };
            return string.IsNullOrWhiteSpace(LoginAtor) ? tipo : $"{tipo} · {LoginAtor}";
        }
    }
}

/// <summary>Resultado paginado da consulta de auditoria + estado (para os estados visuais).</summary>
public sealed record ResultadoAuditoriaVm(
    IReadOnlyList<RegistroAuditoriaVm> Itens,
    int Pagina,
    int TamanhoPagina,
    long Total,
    EstadoConsultaAuditoria Estado,
    string? Mensagem = null);
