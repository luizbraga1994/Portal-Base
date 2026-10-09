using System.Globalization;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Incidentes;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Application.Observabilidade;

/// <summary>
/// PONTE ALERTA → INCIDENTE (Prompt 31, seção 50) — um alerta operacional acima do limiar de severidade origina
/// um <see cref="TipoIncidente.Tecnico"/> no registro de incidentes (P30), preservando a rastreabilidade. NUNCA
/// cria <see cref="TipoIncidente.ComDadosPessoais"/> automaticamente: a classificação de risco/dano ao titular
/// permanece HUMANA (§50). É uma ação do SISTEMA (ator "observabilidade"), auditada, sem payload/PII (§3/§80).
/// Deduplica alertas repetidos: enquanto houver incidente ABERTO para o mesmo código, apenas anexa uma nota (§41).
/// </summary>
public sealed class ServicoPonteAlertaIncidente
{
    private const string AtorSistema = "observabilidade";

    private readonly IRepositorioIncidentes _incidentes;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;
    private readonly OpcoesObservabilidade _opcoes;

    public ServicoPonteAlertaIncidente(IRepositorioIncidentes incidentes, IAuditoria auditoria, IRelogio relogio, OpcoesObservabilidade opcoes)
    {
        ArgumentNullException.ThrowIfNull(incidentes);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);
        _incidentes = incidentes;
        _auditoria = auditoria;
        _relogio = relogio;
        _opcoes = opcoes;
    }

    /// <summary>Retorna o incidente técnico (novo ou o já aberto ao qual o alerta foi anexado), ou <c>null</c> se abaixo do limiar.</summary>
    public async Task<IncidenteSeguranca?> ProcessarAlertaAsync(AlertaOperacional alerta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alerta);
        var agora = _relogio.AgoraUtc;

        // Alerta sempre auditado (accountability operacional — §51); só vira incidente acima do limiar (§50).
        await AuditarAlertaAsync(alerta, agora, cancellationToken).ConfigureAwait(false);
        if ((int)alerta.Severidade < (int)_opcoes.SeveridadeMinimaIncidente)
        {
            return null;
        }

        var codigoBase = MontarCodigo(alerta);
        var existente = await _incidentes.ObterPorCodigoAsync(codigoBase, cancellationToken).ConfigureAwait(false);
        if (existente is { EstaEncerrado: false })
        {
            // Dedup (§41): já há incidente aberto para este código — anexa nota, não cria outro.
            existente.AdicionarNota(AtorSistema, string.Create(CultureInfo.InvariantCulture, $"Alerta recorrente: {alerta.Codigo}."), agora);
            await _incidentes.SalvarAsync(cancellationToken).ConfigureAwait(false);
            return existente;
        }

        // Se já existir um ENCERRADO com o código base (UNIQUE), usa um código único para o novo.
        var codigo = existente is null ? codigoBase : MontarCodigoUnico(alerta);
        var incidente = IncidenteSeguranca.Registrar(
            codigo, alerta.TenantAfetadoId, TipoIncidente.Tecnico, MapearSeveridade(alerta.Severidade),
            $"[{alerta.Componente}] {alerta.Codigo}", alerta.Descricao, alerta.Componente, categoriasDados: null,
            AtorSistema, alerta.CorrelationId, agora);
        await _incidentes.AdicionarAsync(incidente, cancellationToken).ConfigureAwait(false);
        await _incidentes.SalvarAsync(cancellationToken).ConfigureAwait(false);
        await AuditarIncidenteAsync(incidente, agora, cancellationToken).ConfigureAwait(false);
        return incidente;
    }

    private static string MontarCodigo(AlertaOperacional alerta)
    {
        var bruto = $"A-{alerta.Codigo}";
        return bruto.Length > 32 ? bruto[..32] : bruto;
    }

    private static string MontarCodigoUnico(AlertaOperacional alerta)
    {
        var sufixo = Guid.NewGuid().ToString("N")[..6].ToUpper(CultureInfo.InvariantCulture);
        var prefixo = $"A-{alerta.Codigo}";
        var maxPrefixo = 32 - (sufixo.Length + 1);
        if (prefixo.Length > maxPrefixo)
        {
            prefixo = prefixo[..maxPrefixo];
        }

        return $"{prefixo}-{sufixo}";
    }

    private static SeveridadeIncidente MapearSeveridade(SeveridadeAlerta severidade) => severidade switch
    {
        SeveridadeAlerta.Critico => SeveridadeIncidente.Critico,
        SeveridadeAlerta.Alto => SeveridadeIncidente.Alto,
        SeveridadeAlerta.Warning => SeveridadeIncidente.Medio,
        _ => SeveridadeIncidente.Informativo,
    };

    private async Task AuditarAlertaAsync(AlertaOperacional alerta, DateTimeOffset agora, CancellationToken cancellationToken)
        => await _auditoria.RegistrarAsync(
            new EventoAuditoria(AtorSistema, alerta.TenantAfetadoId?.ToString() ?? "plataforma", AcaoAuditoria.AlertaOperacionalDisparado, ResultadoAuditoria.Permitido, agora)
            {
                TipoRecurso = "AlertaOperacional",
                RecursoId = alerta.Codigo,
                Metadados = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["componente"] = alerta.Componente,
                    ["severidade"] = alerta.Severidade.ToString(),
                    ["correlacao"] = alerta.CorrelationId ?? "-",
                },
            },
            cancellationToken).ConfigureAwait(false);

    private async Task AuditarIncidenteAsync(IncidenteSeguranca incidente, DateTimeOffset agora, CancellationToken cancellationToken)
        => await _auditoria.RegistrarAsync(
            new EventoAuditoria(AtorSistema, incidente.TenantAfetadoId?.ToString() ?? "plataforma", AcaoAuditoria.IncidenteRegistrado, ResultadoAuditoria.Permitido, agora)
            {
                TipoRecurso = "IncidenteSeguranca",
                RecursoId = incidente.Id.ToString("N"),
                Metadados = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["incidente"] = incidente.Codigo,
                    ["tipo"] = incidente.Tipo.ToString(),
                    ["severidade"] = incidente.Severidade.ToString(),
                    ["origem"] = "alerta",
                },
            },
            cancellationToken).ConfigureAwait(false);
}
