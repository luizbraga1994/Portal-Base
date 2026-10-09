using System.Globalization;
using Microsoft.Extensions.Logging;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Backup;
using PortalBase.Application.Notificacoes;
using PortalBase.Domain.Backup;

namespace PortalBase.Application.Observabilidade;

/// <summary>Resultado de uma volta da rotina de alertas (para o log e para os testes).</summary>
public sealed record ResultadoCicloAlertas(
    IReadOnlyList<AlertaAvaliado> Ativos, IReadOnlyList<AlertaAvaliado> Disparados, IReadOnlyList<string> Normalizados, IReadOnlyList<FonteAlerta> FontesComFalha);

/// <summary>
/// Uma volta da ROTINA DE ALERTAS (OBS-004, auditoria de 08/10/2026): lê saúde do Worker (catálogo), backup do
/// catálogo, discos e e-mails não enviados; aplica <see cref="AvaliacaoAlertas"/>; e
/// DISPARA cada alerta novo (ou que passou do prazo de repetição) por três caminhos independentes:
/// <list type="number">
/// <item>log Warning/Error (vai para o Visualizador de Eventos do Windows e para o arquivo de log);</item>
/// <item><see cref="ServicoPonteAlertaIncidente"/>: auditoria do alerta e, de Alto para cima, incidente técnico (com dedup);</item>
/// <item>e-mail para <see cref="OpcoesObservabilidade.EmailsAlerta"/> pela fila de disparo (nova tentativa controlada).</item>
/// </list>
/// Uma fonte que falha (banco fora, disco inacessível) é registrada e pulada — as outras seguem; um caminho de disparo
/// que falha não impede os outros. Nada de segredo, caminho de pasta, destinatário ou PII no log (regra 8).
/// Hospedada pela Web (que tem o e-mail e enxerga o Worker parado — o Worker não alerta sobre a própria queda).
/// </summary>
public sealed partial class CicloAlertasOperacionais
{
    /// <summary>O Worker recupera a janela perdida ao subir e o backup leva um tempo: recém-iniciado não é atraso.</summary>
    internal static readonly TimeSpan CarenciaInicioBackup = TimeSpan.FromHours(2);

    private readonly IRepositorioSaudeWorkersPersistida _saude;
    private readonly IRepositorioRegistroBackup _backups;
    private readonly IVerificadorEspacoDisco _discos;
    private readonly ServicoPonteAlertaIncidente _ponte;
    private readonly IFilaEmail _fila;
    private readonly IEstatisticasFilaEmail _estatisticasEmail;
    private readonly EstadoAlertasNotificados _estado;
    private readonly IRelogio _relogio;
    private readonly OpcoesObservabilidade _opcoes;
    private readonly OpcoesBackup _opcoesBackup;
    private readonly ILogger<CicloAlertasOperacionais> _logger;

    public CicloAlertasOperacionais(
        IRepositorioSaudeWorkersPersistida saude,
        IRepositorioRegistroBackup backups,
        IVerificadorEspacoDisco discos,
        ServicoPonteAlertaIncidente ponte,
        IFilaEmail fila,
        IEstatisticasFilaEmail estatisticasEmail,
        EstadoAlertasNotificados estado,
        IRelogio relogio,
        OpcoesObservabilidade opcoes,
        OpcoesBackup opcoesBackup,
        ILogger<CicloAlertasOperacionais> logger)
    {
        ArgumentNullException.ThrowIfNull(saude);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(discos);
        ArgumentNullException.ThrowIfNull(ponte);
        ArgumentNullException.ThrowIfNull(fila);
        ArgumentNullException.ThrowIfNull(estatisticasEmail);
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(opcoesBackup);
        ArgumentNullException.ThrowIfNull(logger);
        _saude = saude;
        _backups = backups;
        _discos = discos;
        _ponte = ponte;
        _fila = fila;
        _estatisticasEmail = estatisticasEmail;
        _estado = estado;
        _relogio = relogio;
        _opcoes = opcoes;
        _opcoesBackup = opcoesBackup;
        _logger = logger;
    }

    /// <summary>Destinatários válidos de <see cref="OpcoesObservabilidade.EmailsAlerta"/> (sem repetição).</summary>
    public static IReadOnlyList<string> Destinatarios(OpcoesObservabilidade opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        return (opcoes.EmailsAlerta ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.Length <= 254 && e.Count(c => c == '@') == 1 && !e.StartsWith('@') && !e.EndsWith('@')
                && e.IndexOfAny(['\r', '\n', ' ', '<', '>']) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ResultadoCicloAlertas> ExecutarAsync(CancellationToken cancellationToken = default)
    {
        var agora = _relogio.AgoraUtc;
        var falhas = new List<FonteAlerta>();
        var entradas = await ColetarAsync(agora, falhas, cancellationToken).ConfigureAwait(false);
        var ativos = AvaliacaoAlertas.Avaliar(entradas, agora, _opcoes);

        var disparados = new List<AlertaAvaliado>();
        foreach (var alerta in ativos)
        {
            if (_estado.DeveNotificar(alerta, agora, _opcoes.RenotificarAlerta))
            {
                await DispararAsync(alerta, agora, cancellationToken).ConfigureAwait(false);
                disparados.Add(alerta);
            }
        }

        var normalizados = _estado.Normalizar(ativos, entradas.FontesAvaliadas);
        foreach (var codigo in normalizados)
        {
            LogNormalizado(_logger, codigo);
        }

        return new ResultadoCicloAlertas(ativos, disparados, normalizados, falhas);
    }

    private async Task<EntradasAlertas> ColetarAsync(DateTimeOffset agora, List<FonteAlerta> falhas, CancellationToken cancellationToken)
    {
        IReadOnlyList<EstadoSaudeWorker>? persistidos = null;
        SituacaoServicoWorker? worker = null;
        try
        {
            persistidos = await _saude.ListarAsync(cancellationToken).ConfigureAwait(false);
            worker = AvaliacaoServicoWorker.Avaliar(persistidos, agora, _opcoes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Falhou(FonteAlerta.Worker, ex, falhas);
        }

        bool? backupRecente = null;
        if (_opcoesBackup.AgendamentoHabilitado)
        {
            try
            {
                backupRecente = await BackupRecenteAsync(persistidos, agora, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Falhou(FonteAlerta.Backup, ex, falhas);
            }
        }

        IReadOnlyList<EspacoDisco>? discos = null;
        try
        {
            discos = _discos.Medir();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Falhou(FonteAlerta.Disco, ex, falhas);
        }

        return new EntradasAlertas
        {
            Worker = worker,
            BackupCatalogoRecente = backupRecente,
            Discos = discos,
            EmailsNaoEnviadosNoCiclo = _estado.DiferencaEmailsNaoEnviados(_estatisticasEmail.NaoEnviados),
        };
    }

    private async Task<bool> BackupRecenteAsync(IReadOnlyList<EstadoSaudeWorker>? persistidos, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var trabalhador = persistidos?.FirstOrDefault(e => string.Equals(e.Worker, ExecucaoBackupAgendado.NomeWorker, StringComparison.Ordinal));
        if (trabalhador is { Ativo: true, IniciadoEmUtc: { } inicio } && agora - inicio < CarenciaInicioBackup)
        {
            return true;
        }

        var prazo = TimeSpan.FromHours(Math.Clamp(_opcoes.AlertaBackupHoras, 2, 24 * 14));
        return await _backups.ExisteConcluidoOuEmAndamentoDesdeAsync(
            TipoBackup.CatalogoCompleto, agora - prazo, agora - ServicoBackup.LimiteEmAndamento, cancellationToken).ConfigureAwait(false);
    }

    private void Falhou(FonteAlerta fonte, Exception ex, List<FonteAlerta> falhas)
    {
        falhas.Add(fonte);
        LogFonteIndisponivel(_logger, fonte, ex.GetType().Name);
    }

    private async Task DispararAsync(AlertaAvaliado avaliado, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var alerta = avaliado.Alerta;

        // 1) Log: Warning/Error chegam ao Visualizador de Eventos (origem PortalBase.Web) e ao arquivo de log.
        if (alerta.Severidade >= SeveridadeAlerta.Alto)
        {
            LogAlertaErro(_logger, alerta.Severidade, alerta.Codigo, alerta.Componente, alerta.Descricao);
        }
        else
        {
            LogAlertaAviso(_logger, alerta.Severidade, alerta.Codigo, alerta.Componente, alerta.Descricao);
        }

        // 2) Auditoria + incidente técnico (de Alto para cima; dedup por código enquanto aberto).
        try
        {
            await _ponte.ProcessarAlertaAsync(alerta, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFalhaIncidente(_logger, alerta.Codigo, ex.GetType().Name);
        }

        // 3) E-mail (nunca para alerta do próprio e-mail).
        if (!avaliado.EnviarPorEmail)
        {
            return;
        }

        var destinatarios = Destinatarios(_opcoes);
        if (destinatarios.Count == 0 || !_fila.Habilitado)
        {
            LogSemEmail(_logger, alerta.Codigo);
            return;
        }

        var assunto = $"[Portal Base] Alerta {Rotulo(alerta.Severidade)}: {alerta.Componente} ({alerta.Codigo})";
        var texto = string.Create(CultureInfo.InvariantCulture, $"""
            Alerta {Rotulo(alerta.Severidade)} do Portal Base - {alerta.Componente}

            {alerta.Descricao}

            Código: {alerta.Codigo}
            Quando: {agora.UtcDateTime:dd/MM/yyyy HH:mm} UTC

            Enquanto o problema persistir, este aviso se repete a cada {_opcoes.RenotificarAlerta.TotalHours:0} h. Alertas de severidade alta ou crítica também abrem um incidente em Auditoria → Incidentes; todos ficam no Visualizador de Eventos do servidor (origem PortalBase.Web).
            """);
        foreach (var para in destinatarios)
        {
            // A fila registra (sem destinatário) quando recusa; nada a fazer aqui além de seguir para o próximo.
            _fila.Enfileirar(new MensagemEmail(para, assunto, texto));
        }
    }

    private static string Rotulo(SeveridadeAlerta severidade) => severidade switch
    {
        SeveridadeAlerta.Critico => "CRÍTICO",
        SeveridadeAlerta.Alto => "alto",
        SeveridadeAlerta.Warning => "de atenção",
        _ => "informativo",
    };

    [LoggerMessage(EventId = 7300, Level = LogLevel.Error, Message = "ALERTA {Severidade} {Codigo} [{Componente}]: {Descricao}")]
    private static partial void LogAlertaErro(ILogger logger, SeveridadeAlerta severidade, string codigo, string componente, string descricao);

    [LoggerMessage(EventId = 7301, Level = LogLevel.Warning, Message = "ALERTA {Severidade} {Codigo} [{Componente}]: {Descricao}")]
    private static partial void LogAlertaAviso(ILogger logger, SeveridadeAlerta severidade, string codigo, string componente, string descricao);

    [LoggerMessage(EventId = 7302, Level = LogLevel.Information, Message = "Alerta {Codigo} normalizado: a condição não está mais presente.")]
    private static partial void LogNormalizado(ILogger logger, string codigo);

    [LoggerMessage(EventId = 7303, Level = LogLevel.Warning, Message = "Rotina de alertas: não foi possível avaliar {Fonte} ({TipoErro}); as outras verificações seguem.")]
    private static partial void LogFonteIndisponivel(ILogger logger, FonteAlerta fonte, string tipoErro);

    [LoggerMessage(EventId = 7304, Level = LogLevel.Warning, Message = "Alerta {Codigo}: não foi possível registrar a auditoria/incidente ({TipoErro}); o log e o e-mail seguem.")]
    private static partial void LogFalhaIncidente(ILogger logger, string codigo, string tipoErro);

    [LoggerMessage(EventId = 7305, Level = LogLevel.Information, Message = "Alerta {Codigo} sem e-mail: Observabilidade:EmailsAlerta vazio ou envio de e-mail desligado.")]
    private static partial void LogSemEmail(ILogger logger, string codigo);
}
