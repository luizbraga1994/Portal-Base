using System.Diagnostics.Metrics;
using FluentAssertions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Observabilidade;
using PortalBase.Infrastructure.Observabilidade;

namespace PortalBase.Integration.Tests.Observabilidade;

/// <summary>
/// Métricas operacionais (Prompt 31, seções 8-10, 66) — o Meter emite os gauges de saúde e, CRÍTICO, os rótulos
/// têm cardinalidade BAIXA e sem PII: as únicas dimensões são "worker"/"integration" (nunca tenant/CNPJ/protocolo/
/// valor — §10). Coletado por <see cref="MeterListener"/>.
/// </summary>
public sealed class MetricasOperacionaisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelogioFixo(DateTimeOffset agora) : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; } = agora;
    }

    private static readonly string[] RotulosProibidos = ["tenant", "tenantid", "cnpj", "cpf", "protocolo", "protocol", "ncm", "valor", "value", "conta"];

    [Fact]
    public void Metricas_TemRotulosControlados_SemPII()
    {
        var workers = new RegistroSaudeWorkers();
        workers.RegistrarSucesso("integracao-externa", 3, T0);
        var integracoes = new RegistroUltimaIntegracao();
        integracoes.RegistrarSucesso("receita-integra", T0);

        using var metricas = new MetricasOperacionais(workers, integracoes, new RelogioFixo(T0.AddSeconds(30)), new OpcoesObservabilidade());

        var chavesRotulos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instrumentos = new List<string>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrumento, ouvinte) =>
            {
                if (instrumento.Meter.Name == MetricasOperacionais.NomeMeter)
                {
                    instrumentos.Add(instrumento.Name);
                    ouvinte.EnableMeasurementEvents(instrumento);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, medida, tags, estado) => RegistrarTags(tags, chavesRotulos));
        listener.SetMeasurementEventCallback<int>((inst, medida, tags, estado) => RegistrarTags(tags, chavesRotulos));
        listener.SetMeasurementEventCallback<double>((inst, medida, tags, estado) => RegistrarTags(tags, chavesRotulos));
        listener.Start();
        listener.RecordObservableInstruments();

        // Emitiu instrumentos operacionais e nenhum rótulo proibido/alta cardinalidade (§9/§10).
        instrumentos.Should().Contain("worker_active");
        instrumentos.Should().Contain("integration_seconds_since_success");
        chavesRotulos.Should().OnlyContain(chave => chave == "worker" || chave == "integration");
        chavesRotulos.Should().NotContain(RotulosProibidos);
    }

    private static void RegistrarTags(ReadOnlySpan<KeyValuePair<string, object?>> tags, HashSet<string> destino)
    {
        foreach (var tag in tags)
        {
            destino.Add(tag.Key);
        }
    }
}
