using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Observabilidade;
using PortalBase.Infrastructure.Observabilidade;
using PortalBase.Infrastructure.Saude;

namespace PortalBase.Integration.Tests.Observabilidade;

/// <summary>
/// Observabilidade operacional (Prompt 31, seções 13-21) — registro de saúde dos workers (heartbeat/estagnação/
/// reinício), registro da última integração (frescor) e os health checks derivados (liveness NUNCA cai por
/// dependência; workers/integrações são Degraded, não Unhealthy). Roda em memória, sem Docker.
/// </summary>
public sealed class ObservabilidadeOperacionalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelogioMutavel(DateTimeOffset inicial) : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; set; } = inicial;
    }

    private static HealthCheckContext Contexto() => new()
    {
        Registration = new HealthCheckRegistration("teste", _ => throw new NotSupportedException(), failureStatus: null, tags: null),
    };

    [Fact] // §20/§21: heartbeat + detecção de estagnação (worker ativo sem batida além do limite).
    public void SaudeWorkers_Heartbeat_E_Estagnacao()
    {
        var registro = new RegistroSaudeWorkers();
        registro.RegistrarInicio("w1", T0);
        registro.RegistrarBatida("w1", T0.AddSeconds(10));
        registro.RegistrarSucesso("w1", 5, T0.AddSeconds(10));

        var estado = registro.Obter("w1")!;
        estado.Ativo.Should().BeTrue();
        estado.TotalProcessado.Should().Be(5);
        estado.Estagnado(TimeSpan.FromMinutes(5), T0.AddSeconds(20)).Should().BeFalse();
        estado.Estagnado(TimeSpan.FromMinutes(5), T0.AddMinutes(10)).Should().BeTrue(); // sem batida há 10min
    }

    [Fact] // §20: reinício é contabilizado; erro guarda só o código (§3).
    public void SaudeWorkers_Reinicio_E_Erro()
    {
        var registro = new RegistroSaudeWorkers();
        registro.RegistrarInicio("w1", T0);
        registro.RegistrarEncerramento("w1", T0.AddSeconds(5));
        registro.RegistrarInicio("w1", T0.AddSeconds(10)); // reinício
        registro.RegistrarErro("w1", "TimeoutException", T0.AddSeconds(20));

        var estado = registro.Obter("w1")!;
        estado.Reinicios.Should().Be(1);
        estado.TotalErros.Should().Be(1);
        estado.UltimoErroCodigo.Should().Be("TimeoutException");
    }

    [Fact] // §16/§44: última integração — frescor do último sucesso.
    public void UltimaIntegracao_Frescor()
    {
        var registro = new RegistroUltimaIntegracao();
        registro.RegistrarSucesso("receita-integra", T0);
        registro.RegistrarFalha("receita-integra", "Http429", T0.AddSeconds(30));
        var estado = registro.Obter("receita-integra")!;
        estado.UltimoSucessoUtc.Should().Be(T0);
        estado.UltimoErroCodigo.Should().Be("Http429");
        estado.TotalSucesso.Should().Be(1);
        estado.TotalFalha.Should().Be(1);
    }

    [Fact] // §14: LIVENESS sempre saudável (processo vivo) — não depende de banco/Receita/cofre.
    public async Task Liveness_SempreSaudavel()
    {
        var resultado = await new VerificacaoVivacidade().CheckHealthAsync(Contexto(), CancellationToken.None);
        resultado.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact] // §21: worker estagnado ⇒ dependency health DEGRADED (não Unhealthy; não reinicia processo saudável).
    public async Task SaudeWorkers_Estagnado_Degraded()
    {
        var registro = new RegistroSaudeWorkers();
        registro.RegistrarInicio("w1", T0);
        var relogio = new RelogioMutavel(T0.AddMinutes(10)); // 10min sem batida
        var check = new VerificacaoSaudeWorkers(registro, relogio, new OpcoesObservabilidade { LimiteEstagnacaoSegundos = 300 });

        var resultado = await check.CheckHealthAsync(Contexto(), CancellationToken.None);
        resultado.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact] // §16: integração sem sucesso recente ⇒ Degraded.
    public async Task UltimaIntegracao_Stale_Degraded()
    {
        var registro = new RegistroUltimaIntegracao();
        registro.RegistrarSucesso("erp-externo", T0);
        var relogio = new RelogioMutavel(T0.AddHours(2)); // além do limite de 1h
        var check = new VerificacaoUltimaIntegracao(registro, relogio, new OpcoesObservabilidade { LimiteIntegracaoStaleSegundos = 3600 });

        var resultado = await check.CheckHealthAsync(Contexto(), CancellationToken.None);
        resultado.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact] // Sem estagnação ⇒ healthy.
    public async Task SaudeWorkers_Ativo_Healthy()
    {
        var registro = new RegistroSaudeWorkers();
        registro.RegistrarBatida("w1", T0);
        var relogio = new RelogioMutavel(T0.AddSeconds(30));
        var check = new VerificacaoSaudeWorkers(registro, relogio, new OpcoesObservabilidade { LimiteEstagnacaoSegundos = 300 });
        (await check.CheckHealthAsync(Contexto(), CancellationToken.None)).Status.Should().Be(HealthStatus.Healthy);
    }
}
