using PortalBase.Application.Autorizacao;
using PortalBase.Application.Consultas;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Arquivos;

namespace PortalBase.Application.Ingestao;

// Read-model de ARQUIVOS DE IMPORTAÇÃO / uploads (Prompt 36, §15) — banco do tenant, somente leitura.
// NUNCA expõe a referência de armazenamento nem o conteúdo do arquivo (só metadados: hash, tamanho, estado).

/// <summary>Campos de ordenação PERMITIDOS para arquivos de importação (whitelist).</summary>
public enum CampoOrdenacaoArquivoImportacao
{
    Enviado = 0,
    Estado = 1,
    Tamanho = 2,
}

/// <summary>Filtro tipado da consulta de arquivos de importação (upload).</summary>
public sealed record FiltroArquivosImportacao
{
    public EstadoArquivo? Estado { get; init; }

    public ResultadoScan? ResultadoScan { get; init; }

    public string? Extensao { get; init; }

    public string? Sha256 { get; init; }

    /// <summary>Limite INCLUSIVO inferior sobre <c>EnviadoEmUtc</c> (envio (upload) do arquivo). Ausente ⇒ sem limite. Prompt 81.</summary>
    public DateTimeOffset? Desde { get; init; }

    /// <summary>Limite INCLUSIVO superior sobre <c>EnviadoEmUtc</c>. <c>Desde &gt; Até</c> é rejeitado no servidor (<see cref="PeriodoConsulta"/>).</summary>
    public DateTimeOffset? Ate { get; init; }

    public CampoOrdenacaoArquivoImportacao Ordenacao { get; init; } = CampoOrdenacaoArquivoImportacao.Enviado;

    public DirecaoOrdenacao Direcao { get; init; } = DirecaoOrdenacao.Descendente;

    public int Pagina { get; init; } = 1;

    public int TamanhoPagina { get; init; } = Paginacao.TamanhoPadrao;

    public FiltroArquivosImportacao Normalizado() => this with
    {
        Pagina = Paginacao.NormalizarPagina(Pagina),
        TamanhoPagina = Paginacao.NormalizarTamanho(TamanhoPagina),
    };
}

/// <summary>Projeção de listagem de um arquivo de importação (metadados — sem referência de armazenamento nem conteúdo).</summary>
public sealed record ItemArquivoImportacao(
    Guid Id,
    string NomeOriginalSanitizado,
    string Extensao,
    string ContentTypeDetectado,
    long TamanhoBytes,
    string Sha256,
    EstadoArquivo Estado,
    ResultadoScan ResultadoScan,
    DateTimeOffset? EscaneadoEmUtc,
    string? MotivoRejeicao,
    DateTimeOffset EnviadoEmUtc,
    DateTimeOffset AtualizadoEmUtc);

/// <summary>Porta de LEITURA de arquivos de importação (upload).</summary>
public interface IConsultaArquivosImportacao
{
    Task<ResultadoPaginado<ItemArquivoImportacao>> ConsultarAsync(FiltroArquivosImportacao filtro, CancellationToken cancellationToken = default);
}

/// <summary>Consulta AUTORIZADA de arquivos de importação (dados.visualizar, tenant fail-closed).</summary>
public sealed class ConsultaArquivosImportacaoAutorizada
{
    private readonly ITenantContext _tenant;
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IConsultaArquivosImportacao _consulta;

    public ConsultaArquivosImportacaoAutorizada(ITenantContext tenant, IServicoAutorizacao autorizacao, IConsultaArquivosImportacao consulta)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(consulta);
        _tenant = tenant;
        _autorizacao = autorizacao;
        _consulta = consulta;
    }

    public async Task<ResultadoPaginado<ItemArquivoImportacao>> ConsultarAsync(FiltroArquivosImportacao filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        await GuardaConsultaTenant.ExigirLeituraAsync(_tenant, _autorizacao, Permissoes.DadosVisualizar, cancellationToken).ConfigureAwait(false);
        PeriodoConsulta.Validar(filtro.Desde, filtro.Ate); // fail-closed ANTES da consulta (nunca só na UI)
        return await _consulta.ConsultarAsync(filtro.Normalizado(), cancellationToken).ConfigureAwait(false);
    }
}
