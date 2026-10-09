using Microsoft.Extensions.Options;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Worker;

/// <summary>
/// HEARTBEAT do host de workers (Prompt 67 — deixa de ser stub). Emite um sinal de LIVENESS sempre-ligado no
/// <see cref="IRegistroSaudeWorkers"/> (início + batida periódica), independente de haver tráfego na Outbox.
/// Assim a estagnação do host é detectável por FRESCOR de timestamp real (§21), não por suposição. Intervalo
/// CONFIGURÁVEL (nunca hardcoded — §17). Não contém regra de negócio; encerra graciosamente.
/// A cada batida também GRAVA no catálogo o snapshot da saúde de TODOS os trabalhadores deste host, para a Web
/// (outro processo) mostrar em Operações se o Worker está vivo. Falha ao gravar não derruba o host: é registrada
/// (só o tipo da exceção) e a próxima batida tenta de novo; se o host morrer, a última batida gravada envelhece e a
/// tela passa a mostrar "estagnado" (frescor real, não suposição).
/// </summary>
public sealed partial class TrabalhadorHeartbeat : BackgroundService
{
    /// <summary>Identidade técnica do sinal de liveness do host (sem PII).</summary>
    public const string NomeWorker = AvaliacaoServicoWorker.WorkerBatida;

    private static readonly TimeSpan PrazoGravacao = TimeSpan.FromSeconds(10);

    private readonly IRegistroSaudeWorkers _saude;
    private readonly IServiceScopeFactory _escopos;
    private readonly OpcoesObservabilidade _opcoes;
    private readonly ILogger<TrabalhadorHeartbeat> _logger;

    public TrabalhadorHeartbeat(IRegistroSaudeWorkers saude, IServiceScopeFactory escopos, IOptions<OpcoesObservabilidade> opcoes, ILogger<TrabalhadorHeartbeat> logger)
    {
        ArgumentNullException.ThrowIfNull(saude);
        ArgumentNullException.ThrowIfNull(escopos);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(logger);
        _saude = saude;
        _escopos = escopos;
        _opcoes = opcoes.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = _opcoes.HeartbeatIntervalo;
        LogIniciado(_logger);
        _saude.RegistrarInicio(NomeWorker, DateTimeOffset.UtcNow);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _saude.RegistrarBatida(NomeWorker, DateTimeOffset.UtcNow); // §21 — batida com timestamp REAL
                LogHeartbeat(_logger);
                await PersistirAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(intervalo, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento solicitado (shutdown gracioso).
        }

        _saude.RegistrarEncerramento(NomeWorker, DateTimeOffset.UtcNow);

        // Grava o encerramento (Ativo = falso) para a tela não mostrar o host como vivo após o shutdown.
        await PersistirAsync(CancellationToken.None).ConfigureAwait(false);
        LogEncerrado(_logger);
    }

    private async Task PersistirAsync(CancellationToken cancellationToken)
    {
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(PrazoGravacao);
        try
        {
            await using var escopo = _escopos.CreateAsyncScope();
            var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioSaudeWorkersPersistida>();
            await repositorio.GravarAsync(_saude.Todos(), DateTimeOffset.UtcNow, prazo.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Encerramento solicitado durante a gravação: a gravação final ocorre após o laço.
        }
#pragma warning disable CA1031 // A saúde persistida é auxiliar: falha (banco fora, prazo) não pode derrubar o host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFalhaPersistir(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Heartbeat do host de workers iniciado.")]
    private static partial void LogIniciado(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat do host de workers.")]
    private static partial void LogHeartbeat(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Não foi possível gravar a saúde dos workers no catálogo ({TipoErro}); nova tentativa na próxima batida.")]
    private static partial void LogFalhaPersistir(ILogger logger, string tipoErro);

    [LoggerMessage(Level = LogLevel.Information, Message = "Heartbeat do host de workers encerrado graciosamente.")]
    private static partial void LogEncerrado(ILogger logger);
}
