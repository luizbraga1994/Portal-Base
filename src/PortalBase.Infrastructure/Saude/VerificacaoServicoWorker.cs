using Microsoft.Extensions.Diagnostics.HealthChecks;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Infrastructure.Saude;

/// <summary>
/// SAÚDE DO SERVIÇO DO WORKER na Web (tag <c>dependencies</c>) — OBS-003 (auditoria de 08/10/2026). Lê a batida que o
/// host do Worker grava no CATÁLOGO (não a memória da Web, onde o Worker não roda) e aplica a regra única de
/// <see cref="AvaliacaoServicoWorker"/>: nunca registrou, parado ou sem sinal ⇒ <see cref="HealthStatus.Unhealthy"/>;
/// trabalhador travado ou em falha ⇒ <see cref="HealthStatus.Degraded"/>. Não derruba readiness (é só dependency
/// health) e não expõe detalhe na resposta HTTP — o motivo aparece em Operações.
/// </summary>
public sealed class VerificacaoServicoWorker : IHealthCheck
{
    private readonly IRepositorioSaudeWorkersPersistida _persistida;
    private readonly IRelogio _relogio;
    private readonly OpcoesObservabilidade _opcoes;

    public VerificacaoServicoWorker(IRepositorioSaudeWorkersPersistida persistida, IRelogio relogio, OpcoesObservabilidade opcoes)
    {
        ArgumentNullException.ThrowIfNull(persistida);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);
        _persistida = persistida;
        _relogio = relogio;
        _opcoes = opcoes;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_opcoes.WorkerEsperado)
        {
            return HealthCheckResult.Healthy("Servidor sem o serviço do Worker (configurado).");
        }

        IReadOnlyList<EstadoSaudeWorker> persistidos;
        try
        {
            persistidos = await _persistida.ListarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            // O catálogo é a readiness: aqui só não dá para afirmar nada sobre o Worker.
            return HealthCheckResult.Degraded("Não foi possível ler a saúde do Worker no catálogo.", excecao);
        }

        var situacao = AvaliacaoServicoWorker.Avaliar(persistidos, _relogio.AgoraUtc, _opcoes);
        var dados = new Dictionary<string, object>(StringComparer.Ordinal) { ["estado"] = situacao.Estado.ToString() };
        return situacao.Estado switch
        {
            EstadoServicoWorker.EmExecucao or EstadoServicoWorker.NaoEsperado => HealthCheckResult.Healthy(situacao.Mensagem, dados),
            EstadoServicoWorker.Degradado => HealthCheckResult.Degraded(situacao.Mensagem, data: dados),
            _ => HealthCheckResult.Unhealthy(situacao.Mensagem, data: dados),
        };
    }
}
