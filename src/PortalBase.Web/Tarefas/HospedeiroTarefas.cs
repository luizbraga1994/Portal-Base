using PortalBase.Application.Tarefas;

namespace PortalBase.Web.Tarefas;

/// <summary>
/// Hospeda o EXECUTOR das tarefas em segundo plano (ARQ-006) na Web — onde já estão a identidade, o cofre e a
/// integração externa. Roda <see cref="OpcoesTarefas.Simultaneas"/> laços: cada um executa a próxima tarefa; sem tarefa,
/// espera o aviso da tela (<see cref="SinalTarefas"/>) ou o intervalo de busca (tarefas enfileiradas por outro processo
/// ou retomadas). Falha inesperada do laço é registrada e espera um pouco mais a cada repetição (até 5 min).
/// </summary>
public sealed partial class HospedeiroTarefas : BackgroundService
{
    private static readonly TimeSpan EsperaMaximaAposFalha = TimeSpan.FromMinutes(5);

    private readonly ExecutorTarefasSegundoPlano _executor;
    private readonly SinalTarefas _sinal;
    private readonly OpcoesTarefas _opcoes;
    private readonly ILogger<HospedeiroTarefas> _logger;

    public HospedeiroTarefas(ExecutorTarefasSegundoPlano executor, SinalTarefas sinal, OpcoesTarefas opcoes, ILogger<HospedeiroTarefas> logger)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(sinal);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(logger);
        _executor = executor;
        _sinal = sinal;
        _opcoes = opcoes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogIniciado(_logger, _opcoes.Simultaneas, (int)_opcoes.IntervaloBusca.TotalSeconds);
        var lacos = Enumerable.Range(0, _opcoes.Simultaneas).Select(_ => LacoAsync(stoppingToken)).ToArray();
        await Task.WhenAll(lacos).ConfigureAwait(false);
    }

    private async Task LacoAsync(CancellationToken stoppingToken)
    {
        var falhasSeguidas = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool executou;
                try
                {
                    executou = await _executor.ExecutarProximaAsync(stoppingToken).ConfigureAwait(false);
                    falhasSeguidas = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // Ex.: catálogo fora do ar. A tarefa (se reivindicada) volta quando o prazo vencer.
                    falhasSeguidas++;
                    LogFalhaLaco(_logger, ex.GetType().Name, falhasSeguidas);
                    await Task.Delay(EsperaAposFalha(falhasSeguidas), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!executou)
                {
                    await _sinal.AguardarNovaAsync(_opcoes.IntervaloBusca, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // desligamento do host: a tarefa em execução fica "em execução" e é retomada quando o prazo vencer
        }
    }

    /// <summary>5 s, 10 s, 20 s... até 5 min.</summary>
    internal static TimeSpan EsperaAposFalha(int falhasSeguidas)
    {
        var segundos = 5d * Math.Pow(2, Math.Clamp(falhasSeguidas - 1, 0, 10));
        var espera = TimeSpan.FromSeconds(segundos);
        return espera < EsperaMaximaAposFalha ? espera : EsperaMaximaAposFalha;
    }

    [LoggerMessage(EventId = 7510, Level = LogLevel.Information, Message = "Executor de tarefas em segundo plano ligado: {Simultaneas} execução(ões) simultânea(s); busca a cada {Segundos} s.")]
    private static partial void LogIniciado(ILogger logger, int simultaneas, int segundos);

    [LoggerMessage(EventId = 7511, Level = LogLevel.Error, Message = "Executor de tarefas: falha no laço ({TipoErro}); {Falhas} falha(s) seguida(s), tenta de novo em seguida.")]
    private static partial void LogFalhaLaco(ILogger logger, string tipoErro, int falhas);
}
