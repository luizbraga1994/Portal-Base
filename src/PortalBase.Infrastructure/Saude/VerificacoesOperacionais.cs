using Microsoft.Extensions.Diagnostics.HealthChecks;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Infrastructure.Saude;

/// <summary>
/// LIVENESS (Prompt 31, seções 13, 14) — responde apenas "o processo está executando?". NÃO verifica banco,
/// APIs externas, cofre ou storage: uma dependência caída NÃO pode causar restart infinito do processo (§14).
/// </summary>
public sealed class VerificacaoVivacidade : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(HealthCheckResult.Healthy("Processo em execução."));
}

/// <summary>
/// SAÚDE DOS WORKERS (Prompt 31, seções 20, 21) — dependency health derivada do registro em memória. Um worker
/// ATIVO sem batida além do limite configurável é possível ESTAGNAÇÃO ⇒ <see cref="HealthStatus.Degraded"/>
/// (não derruba readiness nem reinicia processo saudável — §21/§80). Não expõe payload; só nomes e contadores.
/// </summary>
public sealed class VerificacaoSaudeWorkers : IHealthCheck
{
    private readonly IRegistroSaudeWorkers _registro;
    private readonly IRelogio _relogio;
    private readonly OpcoesObservabilidade _opcoes;

    public VerificacaoSaudeWorkers(IRegistroSaudeWorkers registro, IRelogio relogio, OpcoesObservabilidade opcoes)
    {
        ArgumentNullException.ThrowIfNull(registro);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);
        _registro = registro;
        _relogio = relogio;
        _opcoes = opcoes;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var agora = _relogio.AgoraUtc;
        var limite = _opcoes.LimiteEstagnacao;
        var estagnados = _registro.Todos().Where(w => w.Estagnado(limite, agora)).Select(w => w.Worker).ToList();
        var dados = new Dictionary<string, object>(StringComparer.Ordinal) { ["workers"] = _registro.Todos().Count };

        return Task.FromResult(estagnados.Count == 0
            ? HealthCheckResult.Healthy("Workers ativos sem estagnação.", dados)
            : HealthCheckResult.Degraded($"Workers possivelmente estagnados: {string.Join(",", estagnados)}.", data: dados));
    }
}

/// <summary>
/// SAÚDE DAS INTEGRAÇÕES EXTERNAS (Prompt 31, seções 16, 44) — derivada do FRESCOR do último sucesso (APIs externas),
/// SEM disparar chamada externa por probe (§16/§17). Última integração além do limite ⇒ <see cref="HealthStatus.Degraded"/>.
/// Não expõe credencial/token/payload (§3/§43).
/// </summary>
public sealed class VerificacaoUltimaIntegracao : IHealthCheck
{
    private readonly IRegistroUltimaIntegracao _registro;
    private readonly IRelogio _relogio;
    private readonly OpcoesObservabilidade _opcoes;

    public VerificacaoUltimaIntegracao(IRegistroUltimaIntegracao registro, IRelogio relogio, OpcoesObservabilidade opcoes)
    {
        ArgumentNullException.ThrowIfNull(registro);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);
        _registro = registro;
        _relogio = relogio;
        _opcoes = opcoes;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var agora = _relogio.AgoraUtc;
        var limite = _opcoes.LimiteIntegracaoStale;
        var stale = _registro.Todos()
            .Where(i => i.UltimoSucessoUtc is null || (agora - i.UltimoSucessoUtc.Value) > limite)
            .Select(i => i.Integracao)
            .ToList();

        // Sem nenhuma integração registrada, o estado é "desconhecido/saudável" (nada a monitorar ainda).
        return Task.FromResult(stale.Count == 0
            ? HealthCheckResult.Healthy("Integrações externas recentes.")
            : HealthCheckResult.Degraded($"Integrações sem sucesso recente: {string.Join(",", stale)}."));
    }
}
