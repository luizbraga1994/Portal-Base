using System.Diagnostics.Metrics;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Infrastructure.Observabilidade;

/// <summary>
/// Métricas OPERACIONAIS (Prompt 31, seções 8, 20, 21) via <see cref="Meter"/> — gauges observáveis derivados do
/// registro de saúde dos workers e das integrações. Cardinalidade BAIXA e controlada (§9): a única dimensão é o
/// NOME técnico do worker/integração (conjunto pequeno e fixo) — NUNCA tenant/CNPJ/protocolo/valor (§10). Sem PII.
/// </summary>
public sealed class MetricasOperacionais : IDisposable
{
    public const string NomeMeter = "PortalBase.Operacional";

    private readonly Meter _meter;

    public MetricasOperacionais(IRegistroSaudeWorkers workers, IRegistroUltimaIntegracao integracoes, IRelogio relogio, OpcoesObservabilidade opcoes)
    {
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(integracoes);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);
        _meter = new Meter(NomeMeter);

        _meter.CreateObservableGauge("worker_active", () => workers.Todos()
            .Select(w => new Measurement<int>(w.Ativo ? 1 : 0, Etiqueta("worker", w.Worker))));

        _meter.CreateObservableGauge("worker_stalled", () =>
        {
            var agora = relogio.AgoraUtc;
            var limite = opcoes.LimiteEstagnacao;
            return workers.Todos().Select(w => new Measurement<int>(w.Estagnado(limite, agora) ? 1 : 0, Etiqueta("worker", w.Worker)));
        }, unit: "{worker}", description: "1 se o worker está possivelmente estagnado (§21).");

        _meter.CreateObservableGauge("worker_seconds_since_success", () =>
        {
            var agora = relogio.AgoraUtc;
            return workers.Todos().Select(w => new Measurement<double>(
                w.UltimoSucessoUtc is { } s ? (agora - s).TotalSeconds : -1, Etiqueta("worker", w.Worker)));
        }, unit: "s");

        _meter.CreateObservableCounter("worker_processed_total", () => workers.Todos()
            .Select(w => new Measurement<long>(w.TotalProcessado, Etiqueta("worker", w.Worker))));

        _meter.CreateObservableCounter("worker_errors_total", () => workers.Todos()
            .Select(w => new Measurement<long>(w.TotalErros, Etiqueta("worker", w.Worker))));

        _meter.CreateObservableCounter("worker_restarts_total", () => workers.Todos()
            .Select(w => new Measurement<int>(w.Reinicios, Etiqueta("worker", w.Worker))));

        _meter.CreateObservableGauge("integration_seconds_since_success", () =>
        {
            var agora = relogio.AgoraUtc;
            return integracoes.Todos().Select(i => new Measurement<double>(
                i.UltimoSucessoUtc is { } s ? (agora - s).TotalSeconds : -1, Etiqueta("integration", i.Integracao)));
        }, unit: "s");
    }

    private static KeyValuePair<string, object?> Etiqueta(string chave, string valor) => new(chave, valor);

    public void Dispose() => _meter.Dispose();
}
