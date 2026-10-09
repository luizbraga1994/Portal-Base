using System.Diagnostics.Metrics;
using PortalBase.Application.Provisionamento;

namespace PortalBase.Infrastructure.Provisionamento;

/// <summary>
/// Métricas de provisionamento via <see cref="Meter"/> (Prompt 09, seção 33): contadores de
/// início/sucesso/falha e histogramas de duração, exportáveis por OpenTelemetry. NÃO registra
/// nenhum dado sensível (senha, connection string, segredo) — apenas contagens e durações.
/// </summary>
public sealed class MetricasProvisionamento : IMetricasProvisionamento, IDisposable
{
    public const string NomeMeter = "PortalBase.Provisionamento";

    private readonly Meter _meter;
    private readonly Counter<long> _iniciados;
    private readonly Counter<long> _sucessos;
    private readonly Counter<long> _falhas;
    private readonly Histogram<double> _duracao;
    private readonly Histogram<double> _duracaoMigracao;

    public MetricasProvisionamento()
    {
        _meter = new Meter(NomeMeter);
        _iniciados = _meter.CreateCounter<long>("provisioning_started", unit: "{operação}");
        _sucessos = _meter.CreateCounter<long>("provisioning_success", unit: "{operação}");
        _falhas = _meter.CreateCounter<long>("provisioning_failure", unit: "{operação}");
        _duracao = _meter.CreateHistogram<double>("provisioning_duration", unit: "ms");
        _duracaoMigracao = _meter.CreateHistogram<double>("provisioning_migration_duration", unit: "ms");
    }

    public void RegistrarInicio() => _iniciados.Add(1);

    public void RegistrarSucesso(TimeSpan duracao)
    {
        _sucessos.Add(1);
        _duracao.Record(duracao.TotalMilliseconds);
    }

    public void RegistrarFalha(TimeSpan duracao)
    {
        _falhas.Add(1);
        _duracao.Record(duracao.TotalMilliseconds);
    }

    public void RegistrarDuracaoMigracao(TimeSpan duracao) => _duracaoMigracao.Record(duracao.TotalMilliseconds);

    public void Dispose() => _meter.Dispose();
}
