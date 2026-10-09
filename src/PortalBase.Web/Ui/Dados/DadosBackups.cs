using System.Globalization;
using PortalBase.Application.Backup;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Web.Ui.Dados;

/// <summary>Backup na tela de Operações → Backups (só metadados; nunca local físico, base ou chave).</summary>
public sealed record BackupVm(
    string Alvo,
    string SituacaoRotulo,
    string SituacaoBadge,
    DateTimeOffset IniciadoEmUtc,
    DateTimeOffset? ConcluidoEmUtc,
    string Tamanho,
    bool Criptografado,
    DateTimeOffset? RetencaoAteUtc,
    bool RestoreTestado,
    string? Erro,
    Guid Id = default,
    Guid? TenantId = null,
    bool PodeTestar = false,
    bool PodeRestaurar = false,
    string CopiaExterna = "—",
    string? Hash = null);

/// <summary>Situação do backup no servidor, para a tela (só ligado/desligado — sem caminho, base ou chave).</summary>
public sealed record SituacaoBackupVm(
    bool ExecutorDisponivel,
    bool CifraOk,
    string CifraRotulo,
    bool AgendamentoHabilitado,
    string AgendamentoRotulo,
    bool OffsiteConfigurado,
    int RetencaoDiariaDias,
    int RetencaoSemanalDias,
    int RetencaoMensalDias);

public interface IDadosBackups
{
    Task<(IReadOnlyList<BackupVm> Itens, EstadoConsultaUi Estado)> ListarRecentesAsync(CancellationToken cancellationToken = default);

    Task<(SituacaoBackupVm? Situacao, EstadoConsultaUi Estado)> ObterSituacaoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpcaoContribuinteVm>> ListarContribuintesAsync(CancellationToken cancellationToken = default);

    /// <param name="alvo"><c>catalogo</c>, <c>todos</c> ou o Id de um contribuinte (validado no servidor).</param>
    /// <param name="periodicidade"><c>Diario</c>, <c>Semanal</c> ou <c>Mensal</c>.</param>
    Task<ResultadoAcaoVm> FazerBackupAsync(string? alvo, string? periodicidade, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> TestarRestauracaoAsync(Guid backupId, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> RestaurarContribuinteAsync(Guid backupId, Guid tenantId, string? justificativa, string? confirmacaoNome, CancellationToken cancellationToken = default);
}

/// <summary>
/// Adaptador do backup e da restauração da plataforma. A decisão (autorização, alvo, isolamento, executor) é do
/// servidor; a tela só escolhe e confirma. Entradas do cliente são hostis: alvo e periodicidade de listas fechadas, Ids
/// conferidos no catálogo e o nome digitado na confirmação conferido no servidor. Nenhum detalhe técnico na tela.
/// </summary>
public sealed class DadosBackups : IDadosBackups
{
    private static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("pt-BR");

    private readonly ConsultaBackupsAutorizada _consulta;
    private readonly BackupSobDemanda _backup;
    private readonly RestauracaoPlataforma _restauracao;
    private readonly CadastroContribuinte _contribuintes;

    public DadosBackups(ConsultaBackupsAutorizada consulta, BackupSobDemanda backup, RestauracaoPlataforma restauracao, CadastroContribuinte contribuintes)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(restauracao);
        ArgumentNullException.ThrowIfNull(contribuintes);
        _consulta = consulta;
        _backup = backup;
        _restauracao = restauracao;
        _contribuintes = contribuintes;
    }

    public async Task<(IReadOnlyList<BackupVm> Itens, EstadoConsultaUi Estado)> ListarRecentesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var itens = await _consulta.ListarRecentesAsync(50, cancellationToken).ConfigureAwait(false);
            return (itens.Select(Mapear).ToList(), EstadoConsultaUi.Ok);
        }
        catch (Exception ex) when (ex is NaoAutenticadoException or AutorizacaoNegadaException or MfaRequeridoException)
        {
            return ([], EstadoConsultaUi.SemPermissao);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return ([], EstadoConsultaUi.Erro);
        }
    }

    public async Task<(SituacaoBackupVm? Situacao, EstadoConsultaUi Estado)> ObterSituacaoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var s = await _backup.ObterSituacaoAsync(cancellationToken).ConfigureAwait(false);
            var (cifraOk, cifra) = (s.CriptografarEmRepouso, s.ChaveCriptografiaConfigurada) switch
            {
                (true, true) => (true, "Ligada (chave no cofre)"),
                (true, false) => (false, "Ligada, mas sem chave configurada"),
                _ => (false, "Desligada"),
            };
            return (new SituacaoBackupVm(
                s.ExecutorDisponivel, cifraOk, cifra, s.AgendamentoHabilitado,
                s.AgendamentoHabilitado ? $"Ligado — todo dia às {s.HorarioAgendamentoUtc} (UTC)" : "Desligado",
                s.OffsiteConfigurado, s.RetencaoDiariaDias, s.RetencaoSemanalDias, s.RetencaoMensalDias), EstadoConsultaUi.Ok);
        }
        catch (Exception ex) when (ex is NaoAutenticadoException or AutorizacaoNegadaException or MfaRequeridoException)
        {
            return (null, EstadoConsultaUi.SemPermissao);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return (null, EstadoConsultaUi.Erro);
        }
    }

    public async Task<IReadOnlyList<OpcaoContribuinteVm>> ListarContribuintesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _contribuintes.ListarAsync(cancellationToken).ConfigureAwait(false))
                // Mesma regra do servidor (backup-ready): só contribuinte com base pronta e em uso.
                .Where(c => c.Estado is Domain.Catalogo.EstadoProvisionamento.Ativo or Domain.Catalogo.EstadoProvisionamento.Pronto)
                .Select(c => new OpcaoContribuinteVm(c.Id, c.Nome))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return [];
        }
    }

    public async Task<ResultadoAcaoVm> FazerBackupAsync(string? alvo, string? periodicidade, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<PeriodicidadeBackup>(periodicidade, ignoreCase: false, out var per) || !Enum.IsDefined(per)
            || int.TryParse(periodicidade, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return Invalido("Escolha por quanto tempo guardar o backup.");
        }

        (AlvoBackupSobDemanda Tipo, Guid? Tenant)? escolha = alvo switch
        {
            "catalogo" => (AlvoBackupSobDemanda.Catalogo, null),
            "todos" => (AlvoBackupSobDemanda.TodosContribuintes, null),
            _ when Guid.TryParse(alvo, out var id) && id != Guid.Empty => (AlvoBackupSobDemanda.Contribuinte, id),
            _ => null,
        };
        if (escolha is not { } e)
        {
            return Invalido("Escolha o que copiar.");
        }

        try
        {
            var r = await _backup.ExecutarAsync(e.Tipo, e.Tenant, per, cancellationToken).ConfigureAwait(false);
            if (r.Itens.Count == 0)
            {
                return new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Nada para copiar", "Não há contribuinte com base pronta para backup.");
            }

            return r.ComFalha == 0
                ? new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, "Backup concluído",
                    $"{r.Concluidos} backup(s) concluído(s), cifrado(s) e registrado(s). Veja a lista abaixo.")
                : new ResultadoAcaoVm(false, EstadoAcaoUi.Erro, "Backup com falhas",
                    $"{r.Concluidos} concluído(s) e {r.ComFalha} com falha. Veja a coluna Erro na lista abaixo.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return MapearExcecao(ex, "O backup");
        }
    }

    public async Task<ResultadoAcaoVm> TestarRestauracaoAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        if (backupId == Guid.Empty)
        {
            return Invalido("Backup não identificado.");
        }

        try
        {
            var r = await _restauracao.TestarAsync(backupId, cancellationToken).ConfigureAwait(false);
            return new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, "Restauração testada",
                r.BaseTemporariaRemovida
                    ? "O backup foi restaurado numa base temporária, conferido e a base temporária foi apagada. Os dados em uso não foram tocados."
                    : "O backup foi restaurado e conferido numa base temporária, mas ela não pôde ser apagada — avise a equipe técnica. Os dados em uso não foram tocados.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return MapearExcecao(ex, "O teste de restauração");
        }
    }

    public async Task<ResultadoAcaoVm> RestaurarContribuinteAsync(
        Guid backupId, Guid tenantId, string? justificativa, string? confirmacaoNome, CancellationToken cancellationToken = default)
    {
        if (backupId == Guid.Empty || tenantId == Guid.Empty)
        {
            return Invalido("Backup ou contribuinte não identificado.");
        }

        if ((justificativa ?? string.Empty).Trim().Length < RestauracaoPlataforma.JustificativaMinima)
        {
            return Invalido($"Explique a restauração (mínimo de {RestauracaoPlataforma.JustificativaMinima} caracteres) — fica registrado na auditoria.");
        }

        try
        {
            // Confirmação digitada conferida NO SERVIDOR contra o nome do contribuinte no catálogo.
            var contribuinte = (await _contribuintes.ListarAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.Id == tenantId);
            if (contribuinte is null)
            {
                return Invalido("Contribuinte não encontrado.");
            }

            if (!string.Equals((confirmacaoNome ?? string.Empty).Trim(), contribuinte.Nome, StringComparison.OrdinalIgnoreCase))
            {
                return Invalido($"Para confirmar, digite o nome do contribuinte exatamente como aparece: {contribuinte.Nome}.");
            }

            var r = await _restauracao.RestaurarContribuinteAsync(backupId, tenantId, justificativa, cancellationToken).ConfigureAwait(false);
            return new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, "Contribuinte restaurado",
                $"Os dados de {contribuinte.Nome} voltaram ao backup escolhido e o schema foi atualizado." +
                (r.ContribuinteReativado ? " O contribuinte já está ativo de novo." : string.Empty) +
                " A base anterior foi guardada à parte (a equipe técnica pode recuperá-la).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FalhasUi.Registrar(ex);
            return MapearExcecao(ex, "A restauração");
        }
    }

    public static BackupVm Mapear(ItemBackup b)
    {
        ArgumentNullException.ThrowIfNull(b);
        var contribuinte = b.NomeContribuinte ?? $"Contribuinte {b.TenantId?.ToString("N", CultureInfo.InvariantCulture)[..8]}";
        var alvo = b.Tipo switch
        {
            TipoBackup.CatalogoCompleto => "Catálogo da plataforma",
            TipoBackup.TenantCompleto => contribuinte,
            TipoBackup.Storage => $"Arquivos — {contribuinte}",
            _ => "Arquivos",
        };
        var copia = b.CopiaExterna switch
        {
            SituacaoCopiaExterna.Concluida => "Conferida",
            SituacaoCopiaExterna.Falhou => "Falhou",
            _ => "—",
        };
        var (rotulo, badge) = b.Status switch
        {
            StatusBackup.Concluido => ("Concluído", "ok"),
            StatusBackup.Iniciado => ("Em andamento", "info"),
            StatusBackup.Falhou => ("Falhou", "bad"),
            StatusBackup.Corrompido => ("Corrompido", "bad"),
            StatusBackup.Expirado => ("Expirado", "neutral"),
            _ => (b.Status.ToString(), "neutral"),
        };
        var concluido = b.Status == StatusBackup.Concluido;
        return new BackupVm(alvo, rotulo, badge, b.IniciadoEmUtc, b.ConcluidoEmUtc, Tamanho(b.TamanhoBytes), b.Criptografado,
            b.RetencaoAteUtc, b.RestoreTestado, b.Erro, b.Id, b.TenantId,
            PodeTestar: concluido && b.Tipo is TipoBackup.CatalogoCompleto or TipoBackup.TenantCompleto,
            PodeRestaurar: concluido && b.Tipo == TipoBackup.TenantCompleto && b.TenantId is not null,
            CopiaExterna: copia,
            Hash: b.HashSha256);
    }

    public static string Tamanho(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 => $"{bytes.ToString("N0", Cultura)} B",
        < 1024 * 1024 => $"{(bytes / 1024d).ToString("N1", Cultura)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / (1024d * 1024)).ToString("N1", Cultura)} MB",
        _ => $"{(bytes / (1024d * 1024 * 1024)).ToString("N2", Cultura)} GB",
    };

    /// <summary>Exceção → resultado seguro (nunca stack, SQL, base física ou caminho).</summary>
    public static ResultadoAcaoVm MapearExcecao(Exception excecao, string operacao)
    {
        ArgumentNullException.ThrowIfNull(excecao);
        return excecao switch
        {
            NaoAutenticadoException or AutorizacaoNegadaException => new ResultadoAcaoVm(false, EstadoAcaoUi.SemPermissao, "Sem permissão",
                "Backup e restauração são do Administrador da plataforma (plataforma.backup.executar / plataforma.restore.executar). A decisão é do servidor."),
            MfaRequeridoException => new ResultadoAcaoVm(false, EstadoAcaoUi.MfaRequerida, "Verificação em duas etapas exigida",
                "Esta operação exige o segundo fator (MFA). Confirme e tente novamente."),
            ExecutorBackupIndisponivelException => new ResultadoAcaoVm(false, EstadoAcaoUi.Bloqueado, "Executor de backup indisponível",
                "Este ambiente não tem o executor físico de backup ligado (mysqldump/mysql). Nada foi feito. Peça à equipe técnica para habilitar Backup:ExecutorMySql."),
            BackupBloqueadoPorMigracaoException => new ResultadoAcaoVm(false, EstadoAcaoUi.Bloqueado, "Migração em andamento",
                "Há uma atualização de schema em andamento; para não gerar uma cópia inconsistente, nada foi feito. Tente de novo em alguns minutos."),
            OperacaoBackupEmAndamentoException e => new ResultadoAcaoVm(false, EstadoAcaoUi.Conflito, "Operação em andamento", e.Message),
            RestauracaoCrossTenantException => new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Backup de outro contribuinte",
                "Este backup não pertence a este contribuinte (ou foi tirado de outra base). Nada foi feito; a tentativa ficou na auditoria."),
            BackupCorrompidoException => new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Backup corrompido",
                "A impressão digital (hash) do arquivo não confere: o backup foi marcado como Corrompido e não será usado."),
            MigracaoAposRestauracaoException => new ResultadoAcaoVm(false, EstadoAcaoUi.Erro, "Restauração aplicada, schema pendente",
                "Os dados foram restaurados, mas a atualização do schema falhou; o contribuinte ficou desabilitado. A base anterior está guardada. Avise a equipe técnica."),
            BackupInvalidoException or EntradaInvalidaException => new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Não foi possível", excecao.Message),
            _ => new ResultadoAcaoVm(false, EstadoAcaoUi.Erro, "Não foi possível concluir",
                $"{operacao} falhou. Nada foi alterado nos dados em uso; veja Operações → Workers e os logs do servidor."),
        };
    }

    private static ResultadoAcaoVm Invalido(string mensagem) => new(false, EstadoAcaoUi.Invalido, "Dados inválidos", mensagem);
}
