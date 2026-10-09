using PortalBase.Application.Auditoria;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Web.Observabilidade;

/// <summary>
/// Hospeda a ROTINA DE ALERTAS (OBS-004): dois minutos depois de subir (tempo de o Worker voltar após um reinício do
/// servidor) e então a cada <see cref="OpcoesObservabilidade.IntervaloAlertas"/>, roda uma volta do
/// <see cref="CicloAlertasOperacionais"/> num escopo próprio, como job de sistema na auditoria. Fica na Web porque é ela
/// que envia e-mail e que enxerga o Worker parado (o Worker não alerta sobre a própria queda); o pool do IIS fica
/// sempre ligado (AlwaysRunning, idleTimeout 0, preload). Uma volta que falha é registrada e a próxima segue.
/// </summary>
public sealed partial class MonitorAlertas : BackgroundService
{
    /// <summary>Nome do job na auditoria.</summary>
    public const string NomeJob = "alertas";

    private static readonly TimeSpan AtrasoInicial = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _escopos;
    private readonly OpcoesObservabilidade _opcoes;
    private readonly ILogger<MonitorAlertas> _logger;

    public MonitorAlertas(IServiceScopeFactory escopos, OpcoesObservabilidade opcoes, ILogger<MonitorAlertas> logger)
    {
        ArgumentNullException.ThrowIfNull(escopos);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(logger);
        _escopos = escopos;
        _opcoes = opcoes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opcoes.AlertasHabilitados)
        {
            LogDesligado(_logger);
            return;
        }

        var destinatarios = CicloAlertasOperacionais.Destinatarios(_opcoes).Count;
        if (destinatarios == 0)
        {
            LogSemDestinatarios(_logger);
        }

        LogIniciado(_logger, (int)_opcoes.IntervaloAlertas.TotalMinutes, destinatarios);
        try
        {
            await Task.Delay(AtrasoInicial, stoppingToken).ConfigureAwait(false);
            using var relogio = new PeriodicTimer(_opcoes.IntervaloAlertas);
            do
            {
                await ExecutarVoltaAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await relogio.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // desligamento do host
        }
    }

    /// <summary>Uma volta, num escopo próprio. Nunca lança (exceto no cancelamento): a próxima volta segue.</summary>
    public async Task ExecutarVoltaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var escopo = _escopos.CreateAsyncScope();
            var auditoria = escopo.ServiceProvider.GetRequiredService<IContextoAuditoriaMutavel>();
            auditoria.DefinirOrigem("Web", TipoAtor.JobBackground, NomeJob);
            auditoria.DefinirCorrelacao(Guid.NewGuid().ToString("N"), null);
            var resultado = await escopo.ServiceProvider.GetRequiredService<CicloAlertasOperacionais>()
                .ExecutarAsync(cancellationToken).ConfigureAwait(false);
            LogVolta(_logger, resultado.Ativos.Count, resultado.Disparados.Count, resultado.Normalizados.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFalhaVolta(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 7310, Level = LogLevel.Information, Message = "Rotina de alertas ligada: confere a cada {Minutos} min; {Destinatarios} destinatário(s) de e-mail.")]
    private static partial void LogIniciado(ILogger logger, int minutos, int destinatarios);

    [LoggerMessage(EventId = 7311, Level = LogLevel.Warning, Message = "Rotina de alertas DESLIGADA (Observabilidade:AlertasHabilitados = false): backup atrasado, Worker parado, disco cheio e e-mails não enviados não serão avisados.")]
    private static partial void LogDesligado(ILogger logger);

    [LoggerMessage(EventId = 7312, Level = LogLevel.Warning, Message = "Rotina de alertas sem destinatários de e-mail (Observabilidade:EmailsAlerta vazio): os alertas vão só para o Visualizador de Eventos, o arquivo de log e Auditoria → Incidentes.")]
    private static partial void LogSemDestinatarios(ILogger logger);

    [LoggerMessage(EventId = 7313, Level = LogLevel.Debug, Message = "Rotina de alertas: {Ativos} ativo(s), {Disparados} disparado(s), {Normalizados} normalizado(s).")]
    private static partial void LogVolta(ILogger logger, int ativos, int disparados, int normalizados);

    [LoggerMessage(EventId = 7314, Level = LogLevel.Error, Message = "Rotina de alertas: a volta falhou ({TipoErro}); a próxima segue no intervalo.")]
    private static partial void LogFalhaVolta(ILogger logger, string tipoErro);
}
