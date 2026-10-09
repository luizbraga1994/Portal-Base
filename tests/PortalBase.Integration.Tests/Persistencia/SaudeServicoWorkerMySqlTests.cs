using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Observabilidade;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Infrastructure.Saude;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// OBS-003 (auditoria de 08/10/2026) — o health da Web (<c>/health/dependencies</c>) reflete o serviço do Worker pela
/// batida que ele grava no catálogo REAL (MySQL): no ar ⇒ saudável; parado (serviço encerrado) ou sem sinal (processo que
/// caiu) ⇒ não saudável; trabalhador com a última execução em falha ⇒ degradado. Antes, a verificação lia a memória da
/// Web (vazia) e respondia sempre "saudável".
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class SaudeServicoWorkerMySqlTests
{
    private readonly FixtureMultiTenant _fixture;

    public SaudeServicoWorkerMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Health_Da_Web_Reflete_O_Servico_Do_Worker_Pela_Batida_No_Catalogo()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var agora = DateTimeOffset.UtcNow;
        await LimparAsync(); // a coleção roda em sequência: linhas de outros testes não interferem

        (await VerificarAsync(agora)).Status.Should().Be(HealthStatus.Unhealthy, "sem nenhuma batida o Worker nunca rodou");

        await GravarAsync(agora, new EstadoSaudeWorker(AvaliacaoServicoWorker.WorkerBatida, true, agora.AddHours(-1), agora.AddSeconds(-15), agora.AddSeconds(-15), 100, null, null, 0, 0));
        (await VerificarAsync(agora)).Status.Should().Be(HealthStatus.Healthy);

        await GravarAsync(agora, new EstadoSaudeWorker("backup-agendado", true, agora.AddHours(-1), agora.AddSeconds(-5), agora.AddHours(-30), 1, agora.AddHours(-5), "BackupFalhou", 1, 0));
        var degradado = await VerificarAsync(agora);
        degradado.Status.Should().Be(HealthStatus.Degraded);
        degradado.Description.Should().Contain("backup-agendado");

        await GravarAsync(agora, new EstadoSaudeWorker(AvaliacaoServicoWorker.WorkerBatida, false, agora.AddHours(-1), agora.AddMinutes(-1), agora.AddMinutes(-1), 100, null, null, 0, 0));
        var parado = await VerificarAsync(agora);
        parado.Status.Should().Be(HealthStatus.Unhealthy, "serviço encerrado (Ativo=false gravado no desligamento)");
        parado.Description.Should().Contain("parado");

        await GravarAsync(agora, new EstadoSaudeWorker(AvaliacaoServicoWorker.WorkerBatida, true, agora.AddHours(-1), agora.AddMinutes(-30), agora.AddMinutes(-30), 100, null, null, 0, 0));
        var semSinal = await VerificarAsync(agora);
        semSinal.Status.Should().Be(HealthStatus.Unhealthy, "processo que caiu sem gravar o encerramento");
        semSinal.Description.Should().Contain("não dá sinal");
    }

    private async Task<HealthCheckResult> VerificarAsync(DateTimeOffset agora)
    {
        using var escopo = _fixture.CriarEscopo();
        var verificacao = new VerificacaoServicoWorker(
            escopo.ServiceProvider.GetRequiredService<IRepositorioSaudeWorkersPersistida>(), new Relogio(agora),
            new OpcoesObservabilidade { LimiteEstagnacaoSegundos = 300 });
        return await verificacao.CheckHealthAsync(new HealthCheckContext());
    }

    private async Task GravarAsync(DateTimeOffset agora, EstadoSaudeWorker estado)
    {
        using var escopo = _fixture.CriarEscopo();
        await escopo.ServiceProvider.GetRequiredService<IRepositorioSaudeWorkersPersistida>().GravarAsync([estado], agora);
    }

    private async Task LimparAsync()
    {
        using var escopo = _fixture.CriarEscopo();
        await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().SaudeWorkers.ExecuteDeleteAsync();
    }

    private sealed class Relogio(DateTimeOffset agora) : IRelogio
    {
        public DateTimeOffset AgoraUtc => agora;
    }
}
