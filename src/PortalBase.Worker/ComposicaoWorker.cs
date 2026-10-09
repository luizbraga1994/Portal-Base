using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PortalBase.Application.DependencyInjection;
using PortalBase.Infrastructure.DependencyInjection;
using PortalBase.Infrastructure.Observabilidade;
using Serilog;

namespace PortalBase.Worker;

/// <summary>
/// Composição do host do Worker (logging, telemetria, DI e trabalhadores), extraída do <c>Program</c> para que os
/// testes validem EXATAMENTE a mesma composição que roda em produção — inclusive a validação de DI que o .NET liga
/// em Development (<c>ValidateOnBuild</c>/<c>ValidateScopes</c>).
/// </summary>
public static class ComposicaoWorker
{
    public const string NomeAplicacao = "PortalBase.Worker";

    public static void Configurar(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Servidor próprio: configuração (sem segredo) do scripts/configurar-iis.ps1 em
        // %ProgramData%\PortalBase\PortalBase.Worker.json — a mesma para o serviço e para o .exe aberto à mão.
        PortalBase.Infrastructure.Producao.ConfiguracaoServidor.Adicionar(builder.Configuration, builder.Environment.EnvironmentName, NomeAplicacao);

        // Como serviço do Windows não há console: avisos e erros vão também para o Log de Aplicativo do Windows
        // (origem "PortalBase.Worker"), ligado pelo scripts/configurar-iis.ps1 (Observabilidade:LogEventosWindows).
        builder.Services.AddSerilog((servicos, configuracao) => configuracao
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(servicos)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", NomeAplicacao)
            .Enrich.With(new EnriquecedorRedacaoLogs()) // regra 8
            .GravarNoLogEventosWindowsSeHabilitado(builder.Configuration, NomeAplicacao)
            // Information em diante num arquivo por dia (OBS-001): sem console no IIS/serviço, era o único registro perdido.
            .GravarEmArquivoSeConfigurado(builder.Configuration, NomeAplicacao));

        var otlpConfigurado = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(recurso => recurso.AddService(NomeAplicacao))
            .WithTracing(tracing =>
            {
                tracing.AddSource(PortalBase.Infrastructure.Observabilidade.FonteAtividadePortal.NomeFonte); // spans próprios (Prompt 51)
                if (otlpConfigurado)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(PortalBase.Infrastructure.Provisionamento.MetricasProvisionamento.NomeMeter);
                metrics.AddMeter(PortalBase.Infrastructure.Observabilidade.MetricasOperacionais.NomeMeter); // Prompt 31
                if (otlpConfigurado)
                {
                    metrics.AddOtlpExporter();
                }
            });

        // Pré-checagem de produção (Prompt 33, §9/§10) — FAIL-FAST: aborta se faltar configuração
        // essencial em ambiente produtivo. Nunca lê segredo (só presença). No-op fora de produção.
        PortalBase.Infrastructure.Producao.PreflightProducao.Exigir(
            builder.Configuration, builder.Environment.EnvironmentName, PortalBase.Infrastructure.Producao.ConfiguracaoServidor.Dica(NomeAplicacao));

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(
            builder.Configuration,
            conexaoObrigatoria: builder.Environment.IsProduction(),
            producao: builder.Environment.IsProduction());

        builder.Services.AddHostedService<TrabalhadorHeartbeat>();

        // Backup AGENDADO (catálogo + contribuintes, uma janela por dia; desligado por padrão — Backup:AgendamentoHabilitado).
        builder.Services.AddHostedService<TrabalhadorBackup>();

        // Acrescente aqui os trabalhadores do seu domínio (ex.: fila/outbox de integração por tenant). Cada ciclo abre o
        // próprio escopo, resolve o tenant no servidor e tem timeout, retry controlado e CancellationToken (regra 11).
    }
}
