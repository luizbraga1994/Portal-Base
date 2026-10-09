using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Backup;

/// <summary>O que o Administrador da plataforma pede para copiar agora.</summary>
public enum AlvoBackupSobDemanda
{
    Catalogo = 1,
    Contribuinte = 2,
    TodosContribuintes = 3,
}

/// <summary>Situação do backup no servidor (sem caminho, base, chave ou credencial — só o que está ligado ou não).</summary>
public sealed record SituacaoBackup(
    bool ExecutorDisponivel,
    bool CriptografarEmRepouso,
    bool ChaveCriptografiaConfigurada,
    bool AgendamentoHabilitado,
    string HorarioAgendamentoUtc,
    bool OffsiteConfigurado,
    int RetencaoDiariaDias,
    int RetencaoSemanalDias,
    int RetencaoMensalDias);

/// <summary>Resultado de um backup sob demanda (um ou mais alvos).</summary>
public sealed record ResultadoBackupSobDemanda(int Concluidos, int ComFalha, IReadOnlyList<ResultadoBackup> Itens);

/// <summary>
/// BACKUP SOB DEMANDA pela tela da plataforma: catálogo, um contribuinte ou todos — pelo <see cref="ServicoBackup"/>
/// (mesma autorização de plataforma, cifra, hash, registro e auditoria do backup agendado). O alvo vem do CATÁLOGO
/// (o cliente só escolhe o Id, validado no servidor). Um backup manual por vez (bloqueio no banco, vale entre
/// instâncias). Executor físico fora ⇒ BLOQUEADO, nunca sucesso simulado.
/// </summary>
public sealed class BackupSobDemanda
{
    private const string ChaveBloqueio = "portalbase_backup_manual";

    private readonly ServicoBackup _backup;
    private readonly IProvedorAlvosBackup _alvos;
    private readonly IExecutorBackupBanco _executor;
    private readonly ICriptografiaBackup _cripto;
    private readonly IOperacoesBaseRestauracao _operacoes;
    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly OpcoesBackup _opcoes;
    private readonly ICopiaExternaBackup _copiaExterna;

    public BackupSobDemanda(
        ServicoBackup backup,
        IProvedorAlvosBackup alvos,
        IExecutorBackupBanco executor,
        ICriptografiaBackup cripto,
        IOperacoesBaseRestauracao operacoes,
        IAutorizacaoPlataforma autorizacao,
        OpcoesBackup opcoes,
        ICopiaExternaBackup copiaExterna)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(alvos);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(cripto);
        ArgumentNullException.ThrowIfNull(operacoes);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(copiaExterna);
        _backup = backup;
        _alvos = alvos;
        _executor = executor;
        _cripto = cripto;
        _operacoes = operacoes;
        _autorizacao = autorizacao;
        _opcoes = opcoes;
        _copiaExterna = copiaExterna;
    }

    public async Task<SituacaoBackup> ObterSituacaoAsync(CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);
        return new SituacaoBackup(
            _executor.Disponivel,
            _opcoes.CriptografarEmRepouso,
            !string.IsNullOrWhiteSpace(_cripto.ChaveRef),
            _opcoes.AgendamentoHabilitado,
            _opcoes.HorarioAgendamentoUtc,
            _copiaExterna.Configurada, // destino externo real (as cópias são conferidas pelo hash), não um rótulo
            _opcoes.RetencaoDiariaDias,
            _opcoes.RetencaoSemanalDias,
            _opcoes.RetencaoMensalDias);
    }

    /// <exception cref="EntradaInvalidaException">Alvo, periodicidade ou contribuinte inválidos.</exception>
    /// <exception cref="OperacaoBackupEmAndamentoException">Outro backup manual em andamento.</exception>
    /// <exception cref="ExecutorBackupIndisponivelException">Sem executor físico neste ambiente.</exception>
    public async Task<ResultadoBackupSobDemanda> ExecutarAsync(
        AlvoBackupSobDemanda alvo, Guid? tenantId, PeriodicidadeBackup periodicidade, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);
        if (!Enum.IsDefined(alvo) || !Enum.IsDefined(periodicidade))
        {
            throw new EntradaInvalidaException("Escolha o que copiar e por quanto tempo guardar.");
        }

        if (alvo == AlvoBackupSobDemanda.Contribuinte && (tenantId is null || tenantId == Guid.Empty))
        {
            throw new EntradaInvalidaException("Escolha o contribuinte.");
        }

        await using var bloqueio = await _operacoes.BloquearAsync(ChaveBloqueio, cancellationToken).ConfigureAwait(false);
        // Contribuinte(s): a base E os arquivos (BKP-005). Falha nos arquivos fica registrada e
        // aparece no resultado, sem esconder a base já copiada.
        IReadOnlyList<ResultadoBackup> itens = alvo switch
        {
            AlvoBackupSobDemanda.Catalogo =>
                [await _backup.ExecutarBackupCatalogoAsync(periodicidade, NomeBaseCatalogo(), cancellationToken).ConfigureAwait(false)],
            AlvoBackupSobDemanda.Contribuinte =>
                [
                    await _backup.ExecutarBackupTenantAsync(tenantId!.Value, periodicidade, cancellationToken).ConfigureAwait(false),
                    await ArquivosAsync(tenantId.Value, periodicidade, cancellationToken).ConfigureAwait(false),
                ],
            _ =>
                [
                    .. await _backup.ExecutarBackupTenantsAsync(periodicidade, cancellationToken).ConfigureAwait(false),
                    .. await _backup.ExecutarBackupArquivosTenantsAsync(periodicidade, cancellationToken).ConfigureAwait(false),
                ],
        };

        var concluidos = itens.Count(i => i.Status == StatusBackup.Concluido);
        return new ResultadoBackupSobDemanda(concluidos, itens.Count - concluidos, itens);
    }

    private async Task<ResultadoBackup> ArquivosAsync(Guid tenantId, PeriodicidadeBackup periodicidade, CancellationToken cancellationToken)
    {
        try
        {
            return await _backup.ExecutarBackupArquivosTenantAsync(tenantId, periodicidade, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AutorizacaoNegadaException and not NaoAutenticadoException and not MfaRequeridoException)
        {
            // Já registrado e auditado pelo ServicoBackup; aqui só o tipo do erro (sem caminho — regra 16).
            return new ResultadoBackup(Guid.Empty, TipoBackup.Storage, tenantId, StatusBackup.Falhou, null, null, ex.GetType().Name);
        }
    }

    private string NomeBaseCatalogo()
    {
        var nome = string.IsNullOrWhiteSpace(_opcoes.NomeBaseCatalogo) ? _alvos.NomeBaseCatalogo : _opcoes.NomeBaseCatalogo.Trim();
        return string.IsNullOrWhiteSpace(nome)
            ? throw new BackupInvalidoException("Base do catálogo não configurada no servidor.")
            : nome;
    }
}
