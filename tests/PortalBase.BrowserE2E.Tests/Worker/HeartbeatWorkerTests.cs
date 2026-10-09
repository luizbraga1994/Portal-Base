using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PortalBase.Application.Observabilidade;
using PortalBase.Infrastructure.Observabilidade;
using PortalBase.Worker;

namespace PortalBase.BrowserE2E.Tests.Worker;

/// <summary>
/// Testes do HEARTBEAT do host de workers (Prompt 67) — o <see cref="TrabalhadorHeartbeat"/> deixou de ser um
/// stub e passa a emitir LIVENESS real no <see cref="IRegistroSaudeWorkers"/> (fecha o DSC-11). A verdade
/// operacional depende deste sinal: sem heartbeat, a estagnação do host não é detectável por FRESCOR. Aqui
/// provamos, com o registro REAL (não um fake), que: (1) ao iniciar, o worker registra início + batida com
/// timestamp; (2) a batida é fresca (não estagnada); (3) ao encerrar graciosamente, o estado deixa de ser Ativo
/// (não finge vivo após shutdown). Como o ciclo de vida de um <see cref="BackgroundService"/> é assíncrono,
/// aguardamos o sinal por POLLING limitado (nunca <c>sleep</c> fixo) — determinístico e sem flakiness.
/// Sem PII: a identidade é apenas o nome técnico "heartbeat".
/// </summary>
public sealed class HeartbeatWorkerTests
{
    private static TrabalhadorHeartbeat Criar(IRegistroSaudeWorkers saude, RepositorioSaudeFake? repositorio = null)
    {
        var opcoes = Options.Create(new OpcoesObservabilidade { HeartbeatIntervaloSegundos = 5 });
        var servicos = new ServiceCollection()
            .AddSingleton<IRepositorioSaudeWorkersPersistida>(repositorio ?? new RepositorioSaudeFake())
            .BuildServiceProvider();
        return new TrabalhadorHeartbeat(saude, servicos.GetRequiredService<IServiceScopeFactory>(), opcoes, NullLogger<TrabalhadorHeartbeat>.Instance);
    }

    /// <summary>Repositório de saúde persistida em memória: guarda cada snapshot gravado (ou falha, se pedido).</summary>
    private sealed class RepositorioSaudeFake : IRepositorioSaudeWorkersPersistida
    {
        public ConcurrentQueue<IReadOnlyList<EstadoSaudeWorker>> Gravacoes { get; } = new();

        public bool Falhar { get; init; }

        public Task GravarAsync(IReadOnlyList<EstadoSaudeWorker> estados, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
        {
            if (Falhar)
            {
                throw new InvalidOperationException("banco indisponível");
            }

            Gravacoes.Enqueue(estados.ToList());
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EstadoSaudeWorker>> ListarAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Gravacoes.LastOrDefault() ?? (IReadOnlyList<EstadoSaudeWorker>)[]);
    }

    /// <summary>Aguarda (com timeout) até o worker aparecer no registro de saúde — o start é assíncrono.</summary>
    private static async Task<EstadoSaudeWorker> AguardarRegistroAsync(RegistroSaudeWorkers saude, string nome)
    {
        var relogio = Stopwatch.StartNew();
        while (relogio.Elapsed < TimeSpan.FromSeconds(5))
        {
            var estado = saude.Obter(nome);
            if (estado is not null && estado.UltimaBatidaUtc is not null)
            {
                return estado;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException($"O heartbeat '{nome}' não registrou liveness dentro do tempo esperado.");
    }

    [Fact]
    public async Task AoIniciar_RegistraInicioEBatidaComTimestamp()
    {
        var saude = new RegistroSaudeWorkers();
        var worker = Criar(saude);
        var antes = DateTimeOffset.UtcNow;

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var estado = await AguardarRegistroAsync(saude, TrabalhadorHeartbeat.NomeWorker);
            estado.Ativo.Should().BeTrue();
            estado.IniciadoEmUtc.Should().NotBeNull();
            estado.UltimaBatidaUtc.Should().NotBeNull();
            estado.IniciadoEmUtc.Should().BeOnOrAfter(antes);
            estado.UltimaBatidaUtc.Should().BeOnOrAfter(antes);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NaoEstagnado_LogoAposIniciar()
    {
        var saude = new RegistroSaudeWorkers();
        var worker = Criar(saude);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var estado = await AguardarRegistroAsync(saude, TrabalhadorHeartbeat.NomeWorker);
            // Batida fresca ⇒ não estagnado sob o limite padrão de estagnação (§21).
            estado.Estagnado(TimeSpan.FromSeconds(300), DateTimeOffset.UtcNow).Should().BeFalse();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AoEncerrar_DeixaDeSerAtivo_NaoFingeVivo()
    {
        var saude = new RegistroSaudeWorkers();
        var worker = Criar(saude);

        await worker.StartAsync(CancellationToken.None);
        await AguardarRegistroAsync(saude, TrabalhadorHeartbeat.NomeWorker); // garante que o start já ocorreu
        await worker.StopAsync(CancellationToken.None);

        var estado = saude.Obter(TrabalhadorHeartbeat.NomeWorker);
        estado.Should().NotBeNull();
        // Shutdown gracioso registrado: o host NÃO continua "vivo" após encerrar (verdade operacional — §21).
        estado!.Ativo.Should().BeFalse();
    }

    [Fact]
    public async Task Cada_Batida_Grava_A_Saude_No_Catalogo_E_O_Encerramento_Tambem()
    {
        var saude = new RegistroSaudeWorkers();
        var repositorio = new RepositorioSaudeFake();
        var worker = Criar(saude, repositorio);

        await worker.StartAsync(CancellationToken.None);
        await AguardarRegistroAsync(saude, TrabalhadorHeartbeat.NomeWorker);
        var relogio = Stopwatch.StartNew();
        while (repositorio.Gravacoes.IsEmpty && relogio.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        repositorio.Gravacoes.Should().NotBeEmpty("a Web lê a saúde do Worker pelo catálogo");
        repositorio.Gravacoes.First().Should().Contain(e => e.Worker == TrabalhadorHeartbeat.NomeWorker && e.Ativo);

        await worker.StopAsync(CancellationToken.None);
        repositorio.Gravacoes.Last().Single(e => e.Worker == TrabalhadorHeartbeat.NomeWorker).Ativo
            .Should().BeFalse("o encerramento gracioso também é gravado — a tela não mostra o host vivo depois do shutdown");
    }

    [Fact]
    public async Task Falha_Ao_Gravar_No_Catalogo_Nao_Derruba_O_Heartbeat()
    {
        var saude = new RegistroSaudeWorkers();
        var worker = Criar(saude, new RepositorioSaudeFake { Falhar = true });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var estado = await AguardarRegistroAsync(saude, TrabalhadorHeartbeat.NomeWorker);
            estado.Ativo.Should().BeTrue();
            worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        worker.ExecuteTask!.IsFaulted.Should().BeFalse("falha da gravação auxiliar é registrada, não propagada");
    }

    [Fact]
    public void IntervaloHeartbeat_ConfiguravelEComLimites()
    {
        // Nunca hardcoded, e sempre menor que o limite de estagnação (§17/§21): clamp [5, 300] s.
        new OpcoesObservabilidade { HeartbeatIntervaloSegundos = 30 }.HeartbeatIntervalo.Should().Be(TimeSpan.FromSeconds(30));
        new OpcoesObservabilidade { HeartbeatIntervaloSegundos = 0 }.HeartbeatIntervalo.Should().Be(TimeSpan.FromSeconds(5));
        new OpcoesObservabilidade { HeartbeatIntervaloSegundos = 99999 }.HeartbeatIntervalo.Should().Be(TimeSpan.FromSeconds(300));
    }
}
