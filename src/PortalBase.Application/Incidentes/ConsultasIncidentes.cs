using PortalBase.Application.Autorizacao;
using PortalBase.Application.Consultas;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Application.Incidentes;

// Read-model de INCIDENTES de segurança (Prompt 36, §27). Vive no CATÁLOGO (governança de plataforma,
// cross-tenant) e é autorizado pela trilha de PLATAFORMA (IAutorizacaoPlataforma + seguranca.incidentes.gerir),
// NÃO pela autorização por tenant. A listagem NUNCA expõe evidências protegidas (hash/referência/descrição).

/// <summary>Campos de ordenação PERMITIDOS para incidentes (whitelist).</summary>
public enum CampoOrdenacaoIncidente
{
    Detectado = 0,
    Severidade = 1,
    Status = 2,
    Atualizado = 3,
}

/// <summary>Filtro tipado da consulta de incidentes.</summary>
public sealed record FiltroIncidentes
{
    public TipoIncidente? Tipo { get; init; }

    public SeveridadeIncidente? Severidade { get; init; }

    public StatusIncidente? Status { get; init; }

    /// <summary>Tenant afetado (referência) — filtro, não fronteira de segurança (incidentes são de plataforma).</summary>
    public Guid? TenantAfetadoId { get; init; }

    public string? Codigo { get; init; }

    /// <summary>Limite INCLUSIVO inferior sobre <c>DetectadoEmUtc</c> (detecção do incidente). Ausente ⇒ sem limite. Prompt 81.</summary>
    public DateTimeOffset? Desde { get; init; }

    /// <summary>Limite INCLUSIVO superior sobre <c>DetectadoEmUtc</c>. <c>Desde &gt; Até</c> é rejeitado no servidor (<see cref="PeriodoConsulta"/>).</summary>
    public DateTimeOffset? Ate { get; init; }

    public CampoOrdenacaoIncidente Ordenacao { get; init; } = CampoOrdenacaoIncidente.Detectado;

    public DirecaoOrdenacao Direcao { get; init; } = DirecaoOrdenacao.Descendente;

    public int Pagina { get; init; } = 1;

    public int TamanhoPagina { get; init; } = Paginacao.TamanhoPadrao;

    public FiltroIncidentes Normalizado() => this with
    {
        Pagina = Paginacao.NormalizarPagina(Pagina),
        TamanhoPagina = Paginacao.NormalizarTamanho(TamanhoPagina),
    };
}

/// <summary>Projeção de listagem de um incidente (sem evidências protegidas — hash/referência/descrição).</summary>
public sealed record ItemIncidente(
    Guid Id,
    string Codigo,
    TipoIncidente Tipo,
    SeveridadeIncidente Severidade,
    StatusIncidente Status,
    string Titulo,
    Guid? TenantAfetadoId,
    ClassificacaoRiscoTitular ClassificacaoRisco,
    string? ResponsavelId,
    DateTimeOffset DetectadoEmUtc,
    DateTimeOffset AtualizadoEmUtc,
    DateTimeOffset? EncerradoEmUtc);

/// <summary>
/// Entrada da LINHA DO TEMPO do incidente (quem/quando/o quê) para quem GERE incidentes. A descrição é o texto
/// sanitizado registrado pelo gestor; as evidências protegidas (hash/referência) continuam fora da projeção.
/// </summary>
public sealed record ItemTimelineIncidente(int Sequencia, TipoEntradaTimeline Tipo, string? AtorId, string Descricao, DateTimeOffset OcorridoEmUtc);

/// <summary>Porta de LEITURA de incidentes (Infrastructure implementa sobre o catálogo).</summary>
public interface IConsultaIncidentes
{
    Task<ResultadoPaginado<ItemIncidente>> ConsultarAsync(FiltroIncidentes filtro, CancellationToken cancellationToken = default);
}

/// <summary>
/// Consulta AUTORIZADA de incidentes (Prompt 36, §27). Exige a permissão GLOBAL
/// <c>seguranca.incidentes.gerir</c> pela autorização de PLATAFORMA (não é tenant-escopada). Deny-by-default.
/// </summary>
public sealed class ConsultaIncidentesAutorizada
{
    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly IConsultaIncidentes _consulta;
    private readonly IRepositorioIncidentes _repositorio;

    public ConsultaIncidentesAutorizada(
        IAutorizacaoPlataforma autorizacao,
        IConsultaIncidentes consulta,
        IRepositorioIncidentes repositorio)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(repositorio);
        _autorizacao = autorizacao;
        _consulta = consulta;
        _repositorio = repositorio;
    }

    public async Task<ResultadoPaginado<ItemIncidente>> ConsultarAsync(FiltroIncidentes filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        await _autorizacao.ExigirAsync(Permissoes.SegurancaIncidentesGerir, cancellationToken).ConfigureAwait(false);
        PeriodoConsulta.Validar(filtro.Desde, filtro.Ate); // fail-closed ANTES da consulta (nunca só na UI)
        return await _consulta.ConsultarAsync(filtro.Normalizado(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Detalhe por Id (consulta de PRIMEIRA CLASSE). Autorização de PLATAFORMA (seguranca.incidentes.gerir), deny-by-default.</summary>
    public async Task<ItemIncidente?> ObterAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.SegurancaIncidentesGerir, cancellationToken).ConfigureAwait(false);

        var i = await _repositorio.ObterPorIdAsync(id, cancellationToken).ConfigureAwait(false);
        return i is null
            ? null
            : new ItemIncidente(
                i.Id, i.Codigo, i.Tipo, i.Severidade, i.Status, i.Titulo, i.TenantAfetadoId,
                i.ClassificacaoRisco, i.ResponsavelId, i.DetectadoEmUtc, i.AtualizadoEmUtc, i.EncerradoEmUtc);
    }

    /// <summary>Linha do tempo (append-only) do incidente, em ordem. Mesma autorização de PLATAFORMA; vazio se não existe.</summary>
    public async Task<IReadOnlyList<ItemTimelineIncidente>> ObterTimelineAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.SegurancaIncidentesGerir, cancellationToken).ConfigureAwait(false);

        var i = await _repositorio.ObterPorIdAsync(id, cancellationToken).ConfigureAwait(false);
        return i is null
            ? []
            : i.Timeline.OrderBy(t => t.Sequencia)
                .Select(t => new ItemTimelineIncidente(t.Sequencia, t.Tipo, t.AtorId, t.Descricao, t.OcorridoEmUtc))
                .ToList();
    }
}
