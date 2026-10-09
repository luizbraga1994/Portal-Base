using PortalBase.Domain.Incidentes;

namespace PortalBase.Application.Incidentes;

/// <summary>
/// Repositório de INCIDENTES DE SEGURANÇA (Prompt 30, seções 45-47) — no banco de CATÁLOGO (governança de
/// PLATAFORMA, append-only). O registro é cross-tenant por natureza (o tenant afetado é uma referência), e o
/// acesso é controlado por autorização de plataforma (deny-by-default).
/// </summary>
public interface IRepositorioIncidentes
{
    Task AdicionarAsync(IncidenteSeguranca incidente, CancellationToken cancellationToken = default);

    Task<IncidenteSeguranca?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IncidenteSeguranca?> ObterPorCodigoAsync(string codigo, CancellationToken cancellationToken = default);

    /// <summary>Incidentes mais recentes (para o painel de governança). Limitado, com a timeline carregada.</summary>
    Task<IReadOnlyList<IncidenteSeguranca>> ObterRecentesAsync(int limite, CancellationToken cancellationToken = default);

    Task SalvarAsync(CancellationToken cancellationToken = default);
}

/// <summary>Dados de abertura de um incidente (Prompt 30, seção 45). Descrições SANITIZADAS — nunca payload/PII (§47).</summary>
public sealed record NovoIncidente(
    Guid? TenantAfetadoId, TipoIncidente Tipo, SeveridadeIncidente Severidade, string Titulo, string Descricao,
    string? SistemasAfetados, string? CategoriasDados, string? Codigo = null, string? CorrelationId = null);
