using System.Globalization;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Backup;

namespace PortalBase.Application.Backup;

/// <summary>Desfecho de uma execução do backup agendado (o que o Worker registra em Operações/log).</summary>
public enum DesfechoBackupAgendado
{
    /// <summary>A janela já tem backup do catálogo iniciado/concluído — nada foi feito (idempotente).</summary>
    JaExecutado = 1,

    /// <summary>Catálogo e contribuintes processados (falhas por contribuinte, se houver, contadas à parte).</summary>
    Executado = 2,

    /// <summary>Não executou por motivo operacional conhecido (executor físico fora, migração em andamento).</summary>
    Bloqueado = 3,

    /// <summary>O backup do catálogo falhou (motivo = tipo do erro, sem detalhe sensível).</summary>
    Falhou = 4,
}

/// <summary>
/// Resultado do backup agendado: periodicidade aplicada, contagens e motivo (sem segredo/caminho). Além das bases dos
/// contribuintes: os pacotes de ARQUIVOS (BKP-005), as cópias externas que falharam (BKP-003), os artefatos descartados
/// pela retenção (BKP-004) e os backups interrompidos marcados como falhos (JOB-001).
/// </summary>
public sealed record ResultadoBackupAgendado(
    DesfechoBackupAgendado Desfecho,
    PeriodicidadeBackup Periodicidade,
    int TenantsConcluidos,
    int TenantsComFalha,
    string? Motivo,
    int ArquivosConcluidos = 0,
    int ArquivosComFalha = 0,
    int CopiasExternasComFalha = 0,
    int Descartados = 0,
    int Interrompidos = 0);

/// <summary>
/// Regras de CALENDÁRIO do backup agendado — puras e testáveis. Uma janela por dia, no horário UTC configurado; a
/// periodicidade (que define a retenção) vem do dia da janela: dia 1 ⇒ mensal; domingo ⇒ semanal; demais ⇒ diário.
/// </summary>
public static class AgendaBackup
{
    /// <summary>Lê <c>HH:mm</c> (UTC). Falso para vazio/formato inválido — o chamador não agenda (fail-closed).</summary>
    public static bool TentarLerHorario(string? texto, out TimeOnly horario)
        => TimeOnly.TryParseExact(texto?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out horario);

    /// <summary>Início da janela VIGENTE: o horário de hoje se já passou; senão, o de ontem (UTC).</summary>
    public static DateTimeOffset InicioJanela(TimeOnly horario, DateTimeOffset agoraUtc)
    {
        var agora = agoraUtc.ToUniversalTime();
        var hoje = new DateTimeOffset(DateOnly.FromDateTime(agora.UtcDateTime).ToDateTime(horario), TimeSpan.Zero);
        return agora >= hoje ? hoje : hoje.AddDays(-1);
    }

    /// <summary>Início da PRÓXIMA janela (estritamente depois de <paramref name="agoraUtc"/>).</summary>
    public static DateTimeOffset ProximaJanela(TimeOnly horario, DateTimeOffset agoraUtc)
        => InicioJanela(horario, agoraUtc).AddDays(1);

    /// <summary>Dia 1 ⇒ mensal; domingo ⇒ semanal; demais ⇒ diário (a retenção segue a periodicidade).</summary>
    public static PeriodicidadeBackup Periodicidade(DateTimeOffset inicioJanelaUtc)
    {
        var dia = inicioJanelaUtc.ToUniversalTime();
        if (dia.Day == 1)
        {
            return PeriodicidadeBackup.Mensal;
        }

        return dia.DayOfWeek == DayOfWeek.Sunday ? PeriodicidadeBackup.Semanal : PeriodicidadeBackup.Diario;
    }
}

/// <summary>
/// BACKUP AGENDADO (executado pelo host do Worker): para a janela informada, faz o backup do CATÁLOGO, da base e dos
/// ARQUIVOS de TODOS os contribuintes provisionados e aplica a RETENÇÃO, pelo <see cref="ServicoBackup"/> (mesma
/// autorização de plataforma, cifra, hash, registro, cópia externa e auditoria). IDEMPOTENTE por janela: se já há backup
/// do catálogo CONCLUÍDO (ou em andamento há pouco) desde o início da janela, não faz nada (reinício do Worker ou segunda
/// instância não duplicam); um "Iniciado" antigo de um processo que caiu é marcado como falho antes (JOB-001). O principal é o de SISTEMA
/// <see cref="PrincipalSistema"/>, definido só pelo Worker — a autorização de plataforma concede a ele apenas
/// <c>plataforma.backup.executar</c>. Nunca registra segredo, connection string ou caminho físico.
/// </summary>
public sealed class ExecucaoBackupAgendado
{
    /// <summary>Principal técnico do backup agendado (não é usuário; não autentica pela tela).</summary>
    public const string PrincipalSistema = "sistema:backup-worker";

    /// <summary>Nome técnico do trabalhador do backup agendado na saúde dos workers (Worker e alertas usam o mesmo).</summary>
    public const string NomeWorker = "backup-agendado";

    private readonly ServicoBackup _backup;
    private readonly IRepositorioRegistroBackup _registros;
    private readonly IProvedorAlvosBackup _alvos;
    private readonly OpcoesBackup _opcoes;
    private readonly IRelogio _relogio;

    public ExecucaoBackupAgendado(ServicoBackup backup, IRepositorioRegistroBackup registros, IProvedorAlvosBackup alvos, OpcoesBackup opcoes, IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(registros);
        ArgumentNullException.ThrowIfNull(alvos);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(relogio);
        _backup = backup;
        _registros = registros;
        _alvos = alvos;
        _opcoes = opcoes;
        _relogio = relogio;
    }

    /// <summary>
    /// A janela já foi atendida? Backup do catálogo CONCLUÍDO desde o início dela, ou ainda em andamento há menos de
    /// <see cref="ServicoBackup.LimiteEmAndamento"/>. Um "Iniciado" mais velho que isso não conta (JOB-001).
    /// </summary>
    public Task<bool> JanelaAtendidaAsync(DateTimeOffset inicioJanelaUtc, CancellationToken cancellationToken = default)
        => _registros.ExisteConcluidoOuEmAndamentoDesdeAsync(
            TipoBackup.CatalogoCompleto, inicioJanelaUtc, _relogio.AgoraUtc - ServicoBackup.LimiteEmAndamento, cancellationToken);

    public async Task<ResultadoBackupAgendado> ExecutarJanelaAsync(DateTimeOffset inicioJanelaUtc, CancellationToken cancellationToken = default)
    {
        var periodicidade = AgendaBackup.Periodicidade(inicioJanelaUtc);

        // Backups que ficaram "Iniciado" de um processo que caiu: viram falhos antes de decidir a janela (JOB-001).
        int interrompidos;
        try
        {
            interrompidos = await _backup.MarcarInterrompidosAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AutorizacaoNegadaException or NaoAutenticadoException or MfaRequeridoException)
        {
            return new ResultadoBackupAgendado(DesfechoBackupAgendado.Falhou, periodicidade, 0, 0, "Principal do backup agendado sem autorização.");
        }

        if (await JanelaAtendidaAsync(inicioJanelaUtc, cancellationToken).ConfigureAwait(false))
        {
            return new ResultadoBackupAgendado(DesfechoBackupAgendado.JaExecutado, periodicidade, 0, 0, null);
        }

        var nomeCatalogo = string.IsNullOrWhiteSpace(_opcoes.NomeBaseCatalogo) ? _alvos.NomeBaseCatalogo : _opcoes.NomeBaseCatalogo.Trim();
        if (string.IsNullOrWhiteSpace(nomeCatalogo))
        {
            return new ResultadoBackupAgendado(DesfechoBackupAgendado.Bloqueado, periodicidade, 0, 0, "Base do catálogo não configurada.");
        }

        // Catálogo primeiro: é o mapa dos contribuintes; o registro dele marca a janela como atendida.
        ResultadoBackup catalogo;
        try
        {
            catalogo = await _backup.ExecutarBackupCatalogoAsync(periodicidade, nomeCatalogo, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutorBackupIndisponivelException)
        {
            return new ResultadoBackupAgendado(DesfechoBackupAgendado.Bloqueado, periodicidade, 0, 0, "Executor de backup físico indisponível.");
        }
        catch (BackupBloqueadoPorMigracaoException)
        {
            return new ResultadoBackupAgendado(DesfechoBackupAgendado.Bloqueado, periodicidade, 0, 0, "Migração em andamento.");
        }
        catch (Exception ex) when (ex is AutorizacaoNegadaException or NaoAutenticadoException or MfaRequeridoException)
        {
            return new ResultadoBackupAgendado(DesfechoBackupAgendado.Falhou, periodicidade, 0, 0, "Principal do backup agendado sem autorização.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            // Já registrado/auditado pelo ServicoBackup; aqui só o tipo do erro (sem detalhe sensível — regra 16).
            return new ResultadoBackupAgendado(DesfechoBackupAgendado.Falhou, periodicidade, 0, 0, ex.GetType().Name);
        }

        // Contribuintes: base e arquivos; falha de um não derruba os demais (isolamento já garantido pelo ServicoBackup).
        var tenants = await _backup.ExecutarBackupTenantsAsync(periodicidade, cancellationToken).ConfigureAwait(false);
        var arquivos = await _backup.ExecutarBackupArquivosTenantsAsync(periodicidade, cancellationToken).ConfigureAwait(false);
        var concluidos = tenants.Count(t => t.Status == StatusBackup.Concluido);
        var arquivosConcluidos = arquivos.Count(t => t.Status == StatusBackup.Concluido);
        var copiasComFalha = tenants.Concat(arquivos).Append(catalogo).Count(t => t.CopiaExterna == SituacaoCopiaExterna.Falhou);

        // Retenção depois do backup do dia: nunca descarta o último de um alvo, então um dia sem backup não apaga nada.
        var descartados = await _backup.AplicarRetencaoAsync(cancellationToken).ConfigureAwait(false);
        return new ResultadoBackupAgendado(
            DesfechoBackupAgendado.Executado, periodicidade, concluidos, tenants.Count - concluidos, null,
            arquivosConcluidos, arquivos.Count - arquivosConcluidos, copiasComFalha, descartados, interrompidos);
    }
}
