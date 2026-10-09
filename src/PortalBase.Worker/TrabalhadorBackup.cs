using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Backup;
using PortalBase.Application.Observabilidade;

namespace PortalBase.Worker;

/// <summary>
/// BACKUP AGENDADO: uma vez por dia, no horário UTC configurado (<c>Backup:HorarioAgendamentoUtc</c>), faz o backup do
/// catálogo e de todos os contribuintes provisionados (<see cref="ExecucaoBackupAgendado"/>). Desligado por padrão
/// (<c>Backup:AgendamentoHabilitado</c>). Se o Worker estava parado no horário, recupera a janela perdida ao subir.
/// Idempotente por janela (registro no catálogo); falha/bloqueio ⇒ novas tentativas LIMITADAS e espaçadas na mesma
/// janela. Batida de saúde a cada minuto (Operações mostra vivo/estagnado/erro). Cada execução usa escopo próprio com
/// o principal de sistema — que só tem permissão de backup. Nunca registra segredo, connection string ou caminho.
/// </summary>
public sealed partial class TrabalhadorBackup : BackgroundService
{
    /// <summary>Nome técnico na saúde dos workers (sem PII).</summary>
    public const string NomeWorker = ExecucaoBackupAgendado.NomeWorker;

    private static readonly TimeSpan IntervaloVerificacao = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _escopos;
    private readonly OpcoesBackup _opcoes;
    private readonly IRegistroSaudeWorkers _saude;
    private readonly ILogger<TrabalhadorBackup> _logger;
    private readonly bool _producao;

    private DateTimeOffset? _janela;
    private bool _janelaEncerrada;
    private int _tentativas;
    private DateTimeOffset _ultimaTentativa = DateTimeOffset.MinValue;

    public TrabalhadorBackup(
        IServiceScopeFactory escopos, OpcoesBackup opcoes, IRegistroSaudeWorkers saude, ILogger<TrabalhadorBackup> logger, IHostEnvironment ambiente)
    {
        ArgumentNullException.ThrowIfNull(escopos);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(saude);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ambiente);
        _escopos = escopos;
        _opcoes = opcoes;
        _saude = saude;
        _logger = logger;
        _producao = ambiente.IsProduction();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opcoes.AgendamentoHabilitado)
        {
            if (_producao)
            {
                // Produção sem backup automático (BKP-002): erro visível no Visualizador de Eventos e em Operações.
                _saude.RegistrarErro(NomeWorker, "BackupAgendadoDesligado", DateTimeOffset.UtcNow);
                LogDesabilitadoEmProducao(_logger);
            }
            else
            {
                LogDesabilitado(_logger);
            }

            return;
        }

        _saude.RegistrarInicio(NomeWorker, DateTimeOffset.UtcNow);
        if (!AgendaBackup.TentarLerHorario(_opcoes.HorarioAgendamentoUtc, out var horario))
        {
            // Fail-closed: horário inválido não vira "meia-noite"; o erro fica visível em Operações.
            _saude.RegistrarErro(NomeWorker, "HorarioAgendamentoInvalido", DateTimeOffset.UtcNow);
            LogHorarioInvalido(_logger);
            _saude.RegistrarEncerramento(NomeWorker, DateTimeOffset.UtcNow);
            return;
        }

        LogIniciado(_logger, horario);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _saude.RegistrarBatida(NomeWorker, DateTimeOffset.UtcNow);
                await VerificarAsync(horario, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                await Task.Delay(IntervaloVerificacao, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento solicitado (shutdown gracioso).
        }

        _saude.RegistrarEncerramento(NomeWorker, DateTimeOffset.UtcNow);
        LogEncerrado(_logger);
    }

    /// <summary>Um passo do agendamento: decide se a janela vigente precisa (de nova tentativa) de backup e executa.</summary>
    public async Task VerificarAsync(TimeOnly horario, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var janela = AgendaBackup.InicioJanela(horario, agora);
        if (_janela != janela)
        {
            _janela = janela;
            _janelaEncerrada = false;
            _tentativas = 0;
            _ultimaTentativa = DateTimeOffset.MinValue;
        }

        var maxTentativas = Math.Clamp(_opcoes.TentativasPorJanela, 1, 10);
        var intervalo = TimeSpan.FromMinutes(Math.Clamp(_opcoes.IntervaloEntreTentativasMinutos, 1, 240));
        if (_janelaEncerrada || _tentativas >= maxTentativas || agora - _ultimaTentativa < intervalo)
        {
            return;
        }

        _tentativas++;
        _ultimaTentativa = agora;
        try
        {
            await using var escopo = _escopos.CreateAsyncScope();
            var provedor = escopo.ServiceProvider;
            provedor.GetRequiredService<IContextoSegurancaMutavel>().Definir(ExecucaoBackupAgendado.PrincipalSistema, mfaSatisfeito: false);
            var auditoria = provedor.GetRequiredService<IContextoAuditoriaMutavel>();
            auditoria.DefinirOrigem("Worker", TipoAtor.JobBackground, NomeWorker);
            auditoria.DefinirCorrelacao(Guid.NewGuid().ToString("N"), null);

            var resultado = await provedor.GetRequiredService<ExecucaoBackupAgendado>()
                .ExecutarJanelaAsync(janela, cancellationToken).ConfigureAwait(false);
            switch (resultado.Desfecho)
            {
                case DesfechoBackupAgendado.JaExecutado:
                    _janelaEncerrada = true;
                    break;
                case DesfechoBackupAgendado.Executado:
                    _janelaEncerrada = true;
                    _saude.RegistrarSucesso(NomeWorker, resultado.TenantsConcluidos + resultado.ArquivosConcluidos + 1, DateTimeOffset.UtcNow);
                    if (resultado.TenantsComFalha > 0 || resultado.ArquivosComFalha > 0)
                    {
                        _saude.RegistrarErro(NomeWorker, "BackupContribuinteFalhou", DateTimeOffset.UtcNow);
                    }

                    if (resultado.CopiasExternasComFalha > 0)
                    {
                        _saude.RegistrarErro(NomeWorker, "CopiaExternaFalhou", DateTimeOffset.UtcNow);
                        LogCopiaExternaFalhou(_logger, resultado.CopiasExternasComFalha);
                    }

                    LogExecutado(_logger, resultado.Periodicidade, resultado.TenantsConcluidos, resultado.TenantsComFalha,
                        resultado.ArquivosConcluidos, resultado.ArquivosComFalha, resultado.Descartados, resultado.Interrompidos);
                    if (resultado.TenantsComFalha > 0 || resultado.ArquivosComFalha > 0)
                    {
                        LogFalhasContribuintes(_logger, resultado.TenantsComFalha, resultado.ArquivosComFalha);
                    }

                    break;
                default:
                    _saude.RegistrarErro(NomeWorker, resultado.Desfecho == DesfechoBackupAgendado.Bloqueado ? "BackupBloqueado" : "BackupFalhou", DateTimeOffset.UtcNow);
                    LogNaoExecutado(_logger, resultado.Desfecho, resultado.Motivo ?? "-", _tentativas, maxTentativas);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // O agendamento nunca morre por falha de uma tentativa; a próxima segue o limite.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _saude.RegistrarErro(NomeWorker, ex.GetType().Name, DateTimeOffset.UtcNow);
            LogFalhaTentativa(_logger, ex.GetType().Name, _tentativas, maxTentativas);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup agendado desligado (Backup:AgendamentoHabilitado = false).")]
    private static partial void LogDesabilitado(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "PRODUÇÃO SEM BACKUP AUTOMÁTICO: Backup:AgendamentoHabilitado = false. Rode de novo o scripts/configurar-iis.ps1 (ele liga o backup quando acha o mysqldump) ou ligue Backup:AgendamentoHabilitado e Backup:ExecutorMySql:Habilitado.")]
    private static partial void LogDesabilitadoEmProducao(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup agendado: a cópia externa (offsite) de {Quantidade} artefato(s) falhou ou não conferiu; eles existem só no destino primário. Verifique ArmazenamentoBackup:DiretorioOffsite.")]
    private static partial void LogCopiaExternaFalhou(ILogger logger, int quantidade);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup agendado: {Bases} base(s) e {Arquivos} pacote(s) de arquivos de contribuintes falharam nesta janela (detalhe em Operações → Backups).")]
    private static partial void LogFalhasContribuintes(ILogger logger, int bases, int arquivos);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup agendado NÃO iniciado: Backup:HorarioAgendamentoUtc inválido (use HH:mm).")]
    private static partial void LogHorarioInvalido(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup agendado iniciado: diariamente às {Horario} UTC.")]
    private static partial void LogIniciado(ILogger logger, TimeOnly horario);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup agendado concluído ({Periodicidade}): bases de {Concluidos} contribuinte(s) com backup, {Falhas} com falha; arquivos de {Arquivos} contribuinte(s), {FalhasArquivos} com falha; {Descartados} artefato(s) descartado(s) pela retenção; {Interrompidos} backup(s) interrompido(s) marcado(s) como falho(s).")]
    private static partial void LogExecutado(ILogger logger, PeriodicidadeBackup periodicidade, int concluidos, int falhas, int arquivos, int falhasArquivos, int descartados, int interrompidos);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup agendado não executado ({Desfecho}: {Motivo}); tentativa {Tentativa} de {Maximo} nesta janela.")]
    private static partial void LogNaoExecutado(ILogger logger, DesfechoBackupAgendado desfecho, string motivo, int tentativa, int maximo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup agendado falhou ({TipoErro}); tentativa {Tentativa} de {Maximo} nesta janela.")]
    private static partial void LogFalhaTentativa(ILogger logger, string tipoErro, int tentativa, int maximo);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup agendado encerrado graciosamente.")]
    private static partial void LogEncerrado(ILogger logger);
}
