using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;

namespace PortalBase.Application.Observabilidade;

// Read-model de OPERAÇÕES (Prompt 36, §28) — SOMENTE LEITURA de um snapshot dos registros in-memory de
// observabilidade (Prompt 31): saúde de workers, última integração e derivação de saúde (estagnação/stale).
// NÃO expõe comando algum (não inicia/para worker). NÃO carrega PII/segredo (os próprios registros já são
// livres disso). Autorizado pela trilha de PLATAFORMA (seguranca.incidentes.gerir) — é visão cross-tenant.
// O snapshot é pequeno (poucos workers/integrações) e não paginado por natureza (§41).

/// <summary>Projeção de saúde de um worker, com o veredito de estagnação já calculado.</summary>
public sealed record ItemSaudeWorker(
    string Worker,
    bool Ativo,
    bool Estagnado,
    DateTimeOffset? IniciadoEmUtc,
    DateTimeOffset? UltimaBatidaUtc,
    DateTimeOffset? UltimoSucessoUtc,
    long TotalProcessado,
    DateTimeOffset? UltimoErroUtc,
    string? UltimoErroCodigo,
    long TotalErros,
    int Reinicios);

/// <summary>Projeção da última integração externa, com o veredito de "degradada" (frescor) já calculado.</summary>
public sealed record ItemIntegracao(
    string Integracao,
    bool Degradada,
    DateTimeOffset? UltimoSucessoUtc,
    DateTimeOffset? UltimaFalhaUtc,
    string? UltimoErroCodigo,
    long TotalSucesso,
    long TotalFalha);

/// <summary>Painel operacional (snapshot): workers, integrações e resumos derivados.</summary>
public sealed record PainelOperacoes(
    IReadOnlyList<ItemSaudeWorker> Workers,
    IReadOnlyList<ItemIntegracao> Integracoes,
    int WorkersEstagnados,
    int IntegracoesDegradadas,
    DateTimeOffset GeradoEmUtc)
{
    /// <summary>Situação do SERVIÇO do Worker pela batida gravada no catálogo (OBS-003).</summary>
    public SituacaoServicoWorker? ServicoWorker { get; init; }
}

/// <summary>
/// Consulta AUTORIZADA do painel de operações (Prompt 36, §28). Exige a permissão GLOBAL
/// <c>seguranca.incidentes.gerir</c> pela autorização de PLATAFORMA (visão cross-tenant; deny-by-default).
/// Só LEITURA — jamais comanda os workers.
/// </summary>
public sealed class ConsultaOperacoesAutorizada
{
    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly IRegistroSaudeWorkers _saudeWorkers;
    private readonly IRepositorioSaudeWorkersPersistida _saudePersistida;
    private readonly IRegistroUltimaIntegracao _integracoes;
    private readonly OpcoesObservabilidade _opcoes;
    private readonly IRelogio _relogio;

    public ConsultaOperacoesAutorizada(
        IAutorizacaoPlataforma autorizacao,
        IRegistroSaudeWorkers saudeWorkers,
        IRepositorioSaudeWorkersPersistida saudePersistida,
        IRegistroUltimaIntegracao integracoes,
        OpcoesObservabilidade opcoes,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(saudeWorkers);
        ArgumentNullException.ThrowIfNull(saudePersistida);
        ArgumentNullException.ThrowIfNull(integracoes);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(relogio);
        _autorizacao = autorizacao;
        _saudeWorkers = saudeWorkers;
        _saudePersistida = saudePersistida;
        _integracoes = integracoes;
        _opcoes = opcoes;
        _relogio = relogio;
    }

    public async Task<PainelOperacoes> ObterPainelAsync(CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.SegurancaIncidentesGerir, cancellationToken).ConfigureAwait(false);

        var agora = _relogio.AgoraUtc;

        // Workers do PRÓPRIO processo (memória) + os do host do Worker (persistidos a cada batida); o persistido
        // prevalece para o mesmo nome (é o host que de fato roda aquele trabalhador).
        var persistidos = await _saudePersistida.ListarAsync(cancellationToken).ConfigureAwait(false);
        var estados = _saudeWorkers.Todos()
            .Where(w => !persistidos.Any(p => string.Equals(p.Worker, w.Worker, StringComparison.Ordinal)))
            .Concat(persistidos);
        var workers = estados
            .Select(w => new ItemSaudeWorker(
                w.Worker, w.Ativo, w.Estagnado(_opcoes.LimiteEstagnacao, agora), w.IniciadoEmUtc, w.UltimaBatidaUtc,
                w.UltimoSucessoUtc, w.TotalProcessado, w.UltimoErroUtc, w.UltimoErroCodigo, w.TotalErros, w.Reinicios))
            .OrderBy(w => w.Worker, StringComparer.Ordinal)
            .ToList();

        var integracoes = _integracoes.Todos()
            .Select(i => new ItemIntegracao(
                i.Integracao, IntegracaoDegradada(i.UltimoSucessoUtc, agora), i.UltimoSucessoUtc, i.UltimaFalhaUtc,
                i.UltimoErroCodigo, i.TotalSucesso, i.TotalFalha))
            .OrderBy(i => i.Integracao, StringComparer.Ordinal)
            .ToList();

        return new PainelOperacoes(
            workers,
            integracoes,
            workers.Count(w => w.Estagnado),
            integracoes.Count(i => i.Degradada),
            agora)
        {
            ServicoWorker = AvaliacaoServicoWorker.Avaliar(persistidos, agora, _opcoes),
        };
    }

    private bool IntegracaoDegradada(DateTimeOffset? ultimoSucessoUtc, DateTimeOffset agoraUtc)
        => ultimoSucessoUtc is not { } ultimo || (agoraUtc - ultimo) > _opcoes.LimiteIntegracaoStale;
}
