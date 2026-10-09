using System.Globalization;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Ingestao;
using PortalBase.Domain.Backup;

namespace PortalBase.Application.Backup;

/// <summary>Resultado de um backup individual (Prompt 50) — metadados, nunca conteúdo/segredo.</summary>
public sealed record ResultadoBackup(
    Guid BackupId,
    TipoBackup Tipo,
    Guid? TenantId,
    StatusBackup Status,
    string? Hash,
    string? Local,
    string? Erro,
    SituacaoCopiaExterna? CopiaExterna = null);

/// <summary>
/// SERVIÇO DE BACKUP (Prompt 50, Fases 3-13) — orquestra a geração de backups do CATÁLOGO, de cada TENANT (base) e
/// dos ARQUIVOS de cada tenant respeitando a arquitetura CATALOG DB + DATABASE-PER-TENANT. Pipeline por alvo:
/// <c>conteúdo → cifra (cofre) → hash SHA-256 → grava no store → registra → cópia externa conferida → audita</c>.
/// Garantias: autorização de PLATAFORMA (nunca usuário comum); paralelismo LIMITADO (Fase 6); pula
/// tenant não provisionado (Fase 7); bloqueia durante migration quando a política manda (Fase 8);
/// isolamento A→Backup A / B→Backup B (Fase 5); fail-closed quando o executor físico está indisponível
/// (BLOCKED, nunca simulado como sucesso — Fase 33). NUNCA registra segredo/chave.
/// Auditoria de 08/10/2026: o conteúdo passa por ARQUIVOS TEMPORÁRIOS privados (nunca a base inteira em memória —
/// JOB-002); cancelamento/tempo esgotado marca o registro como falho (JOB-001); cópia externa conferida pelo hash
/// (BKP-003); backup dos arquivos (BKP-005); retenção aplicada sem nunca apagar o último backup do alvo (BKP-004).
/// </summary>
public sealed class ServicoBackup
{
    private const string VersaoArtefato = "portalbase-2.0";

    private readonly IProvedorAlvosBackup _alvos;
    private readonly IExecutorBackupBanco _executor;
    private readonly IArmazenamentoBackup _store;
    private readonly ICriptografiaBackup _cripto;
    private readonly IRepositorioRegistroBackup _repositorio;
    private readonly IDetectorMigracaoEmAndamento _migracao;
    private readonly PoliticaRetencaoBackup _retencao;
    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly IAuditoria _auditoria;
    private readonly IContextoSeguranca _seguranca;
    private readonly IRelogio _relogio;
    private readonly OpcoesBackup _opcoes;
    private readonly IAreaTrabalhoTemporaria _temporarios;
    private readonly ICopiaExternaBackup _copiaExterna;
    private readonly IEmpacotadorArquivosTenant _empacotador;

    // Uma operação por vez no CATÁLOGO: os backups das bases rodam em paralelo (ParalelismoMaximo), mas o repositório,
    // a auditoria e o detector de migração usam o MESMO DbContext do escopo, que não aceita duas operações simultâneas
    // (defeito achado na Onda 2, 08/10/2026). Só o trabalho pesado (dump, cifra, hash, gravação) fica em paralelo.
    private readonly Lock _travaCatalogo = new();
    private Task _filaCatalogo = Task.CompletedTask;

    public ServicoBackup(
        IProvedorAlvosBackup alvos,
        IExecutorBackupBanco executor,
        IArmazenamentoBackup store,
        ICriptografiaBackup cripto,
        IRepositorioRegistroBackup repositorio,
        IDetectorMigracaoEmAndamento migracao,
        PoliticaRetencaoBackup retencao,
        IAutorizacaoPlataforma autorizacao,
        IAuditoria auditoria,
        IContextoSeguranca seguranca,
        IRelogio relogio,
        OpcoesBackup opcoes,
        IAreaTrabalhoTemporaria temporarios,
        ICopiaExternaBackup copiaExterna,
        IEmpacotadorArquivosTenant empacotador)
    {
        ArgumentNullException.ThrowIfNull(alvos);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cripto);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(migracao);
        ArgumentNullException.ThrowIfNull(retencao);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(temporarios);
        ArgumentNullException.ThrowIfNull(copiaExterna);
        ArgumentNullException.ThrowIfNull(empacotador);

        _alvos = alvos;
        _executor = executor;
        _store = store;
        _cripto = cripto;
        _repositorio = repositorio;
        _migracao = migracao;
        _retencao = retencao;
        _autorizacao = autorizacao;
        _auditoria = auditoria;
        _seguranca = seguranca;
        _relogio = relogio;
        _opcoes = opcoes;
        _temporarios = temporarios;
        _copiaExterna = copiaExterna;
        _empacotador = empacotador;
    }

    /// <summary>Limite acima do qual um backup ainda "Iniciado" é considerado interrompido (muito acima do tempo de um dump).</summary>
    public static readonly TimeSpan LimiteEmAndamento = TimeSpan.FromHours(6);

    /// <summary>Backup completo do CATÁLOGO. Autoriza plataforma; bloqueia se migration em andamento (Fase 8).</summary>
    public async Task<ResultadoBackup> ExecutarBackupCatalogoAsync(
        PeriodicidadeBackup periodicidade,
        string nomeBaseCatalogo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeBaseCatalogo);
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);
        return await RealizarBackupBancoAsync(TipoBackup.CatalogoCompleto, null, nomeBaseCatalogo, periodicidade, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Backup de TODOS os tenants backup-ready (Fase 5-7). Paralelismo limitado (Fase 6); falha de um
    /// tenant é registrada e NÃO aborta os demais (isolamento). Tenant não provisionado é pulado.
    /// </summary>
    public async Task<IReadOnlyList<ResultadoBackup>> ExecutarBackupTenantsAsync(
        PeriodicidadeBackup periodicidade,
        CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);

        var alvos = await _alvos.ListarBackupReadyAsync(cancellationToken).ConfigureAwait(false);
        var backupReady = alvos.Where(a => a.BackupReady).ToList();
        if (backupReady.Count == 0)
        {
            return [];
        }

        using var limite = new SemaphoreSlim(Math.Max(1, _opcoes.ParalelismoMaximo));
        var tarefas = backupReady.Select(async alvo =>
        {
            await limite.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RealizarBackupBancoAsync(TipoBackup.TenantCompleto, alvo.TenantId, alvo.NomeBaseFisica, periodicidade, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Isolamento: a falha de um tenant não derruba os demais (a falha já foi auditada/registrada).
                return new ResultadoBackup(Guid.Empty, TipoBackup.TenantCompleto, alvo.TenantId, StatusBackup.Falhou, null, null, Sanitizar(ex));
            }
            finally
            {
                limite.Release();
            }
        });

        return await Task.WhenAll(tarefas).ConfigureAwait(false);
    }

    /// <summary>
    /// Backup de UM tenant (sob demanda, pela tela da plataforma). Só tenant provisionado (backup-ready); o alvo vem do
    /// catálogo (nunca do cliente). Mesmo pipeline/garantias do backup de todos.
    /// </summary>
    public async Task<ResultadoBackup> ExecutarBackupTenantAsync(
        Guid tenantId,
        PeriodicidadeBackup periodicidade,
        CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);

        var alvo = await _alvos.ObterAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (alvo is null || !alvo.BackupReady)
        {
            throw new BackupInvalidoException("Contribuinte inexistente, desabilitado ou ainda não provisionado (sem base pronta para backup).");
        }

        return await RealizarBackupBancoAsync(TipoBackup.TenantCompleto, alvo.TenantId, alvo.NomeBaseFisica, periodicidade, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Backup dos ARQUIVOS (planilhas enviadas e payloads oficiais de retorno) de TODOS os tenants backup-ready
    /// (BKP-005): um artefato por contribuinte, cifrado como os demais. Falha de um não derruba os outros.
    /// </summary>
    public async Task<IReadOnlyList<ResultadoBackup>> ExecutarBackupArquivosTenantsAsync(
        PeriodicidadeBackup periodicidade,
        CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);

        var alvos = await _alvos.ListarBackupReadyAsync(cancellationToken).ConfigureAwait(false);
        var resultados = new List<ResultadoBackup>();
        foreach (var alvo in alvos.Where(a => a.BackupReady))
        {
            // Em série: o gargalo é o disco (os bancos já foram copiados com paralelismo limitado).
            try
            {
                resultados.Add(await RealizarBackupArquivosAsync(alvo.TenantId, periodicidade, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resultados.Add(new ResultadoBackup(Guid.Empty, TipoBackup.Storage, alvo.TenantId, StatusBackup.Falhou, null, null, Sanitizar(ex)));
            }
        }

        return resultados;
    }

    /// <summary>
    /// Backup dos ARQUIVOS de UM tenant (sob demanda, pela tela). Mesmas regras do backup da base: só tenant
    /// provisionado; o alvo vem do catálogo, nunca do cliente.
    /// </summary>
    public async Task<ResultadoBackup> ExecutarBackupArquivosTenantAsync(
        Guid tenantId,
        PeriodicidadeBackup periodicidade,
        CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);

        var alvo = await _alvos.ObterAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (alvo is null || !alvo.BackupReady)
        {
            throw new BackupInvalidoException("Contribuinte inexistente, desabilitado ou ainda não provisionado (sem base pronta para backup).");
        }

        return await RealizarBackupArquivosAsync(alvo.TenantId, periodicidade, cancellationToken).ConfigureAwait(false);
    }

    private Task<ResultadoBackup> RealizarBackupArquivosAsync(Guid tenantId, PeriodicidadeBackup periodicidade, CancellationToken cancellationToken)
        => RealizarBackupAsync(
            TipoBackup.Storage,
            tenantId,
            "arquivos",
            periodicidade,
            (destino, ct) => _empacotador.EmpacotarAsync(tenantId, destino, ct),
            cancellationToken);

    /// <summary>
    /// Marca como FALHOU os backups que ficaram "Iniciado" por mais de <see cref="LimiteEmAndamento"/> — o processo
    /// caiu no meio (JOB-001). Sem isso a tela mostraria "em andamento" para sempre.
    /// </summary>
    public async Task<int> MarcarInterrompidosAsync(CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);

        var agora = _relogio.AgoraUtc;
        return await NoCatalogoAsync(async () =>
        {
            var interrompidos = await _repositorio.ListarIniciadosAntesDeAsync(agora - LimiteEmAndamento, cancellationToken).ConfigureAwait(false);
            foreach (var registro in interrompidos)
            {
                registro.Falhar("Interrompido", agora);
                await _repositorio.AtualizarAsync(registro, cancellationToken).ConfigureAwait(false);
                await AuditarSemFilaAsync(AcaoAuditoria.BackupFailed, registro.Tipo, registro.TenantId, registro.Id, null, ResultadoAuditoria.Negado, cancellationToken)
                    .ConfigureAwait(false);
            }

            return interrompidos.Count;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// RETENÇÃO (BKP-004): descarta os artefatos fora da janela de retenção — primário e cópia externa — e marca o
    /// registro como Expirado. NUNCA o último backup concluído de um alvo (o mais novo fica, mesmo vencido). Um artefato
    /// que não pôde ser apagado continua Concluído (tenta de novo na próxima vez).
    /// </summary>
    public async Task<int> AplicarRetencaoAsync(CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);

        var agora = _relogio.AgoraUtc;
        return await NoCatalogoAsync(() => DescartarVencidosAsync(agora, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<int> DescartarVencidosAsync(DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var vencidos = await _repositorio.ListarConcluidosVencidosAsync(agora, cancellationToken).ConfigureAwait(false);
        var descartados = 0;
        foreach (var alvo in vencidos.GroupBy(r => (r.Tipo, r.TenantId)))
        {
            // Todos os concluídos do alvo (vencidos ou não): o mais novo nunca sai, mesmo vencido.
            var restantes = await _repositorio.ContarConcluidosAsync(alvo.Key.TenantId, alvo.Key.Tipo, cancellationToken).ConfigureAwait(false);
            // Do mais velho ao mais novo: se todos venceram, o que sobra é o mais recente.
            foreach (var registro in alvo.OrderBy(r => r.IniciadoEmUtc))
            {
                if (!PoliticaRetencaoBackup.PodeDescartar(registro, restantes, agora))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(registro.LocalArmazenamentoLogico))
                {
                    continue;
                }

                try
                {
                    await _store.RemoverAsync(registro.LocalArmazenamentoLogico, cancellationToken).ConfigureAwait(false);
                    if (_copiaExterna.Configurada)
                    {
                        await _copiaExterna.RemoverAsync(registro.LocalArmazenamentoLogico, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // fica Concluído; a próxima execução tenta de novo
                }

                registro.Expirar(agora);
                await _repositorio.AtualizarAsync(registro, cancellationToken).ConfigureAwait(false);
                await AuditarSemFilaAsync(AcaoAuditoria.BackupPruned, registro.Tipo, registro.TenantId, registro.Id, registro.HashSha256, ResultadoAuditoria.Permitido, cancellationToken)
                    .ConfigureAwait(false);
                restantes--;
                descartados++;
            }
        }

        return descartados;
    }

    private async Task<ResultadoBackup> RealizarBackupBancoAsync(
        TipoBackup tipo,
        Guid? tenantId,
        string alvoLogico,
        PeriodicidadeBackup periodicidade,
        CancellationToken cancellationToken)
    {
        // Fase 8 — não gerar backup inconsistente durante migration (quando a política manda bloquear).
        if (_opcoes.BloquearDuranteMigracao
            && await NoCatalogoAsync(() => _migracao.MigracaoEmAndamentoAsync(tenantId, cancellationToken)).ConfigureAwait(false))
        {
            throw new BackupBloqueadoPorMigracaoException();
        }

        // Fase 33 — sem MySQL/ferramenta, o backup FÍSICO é BLOCKED (nunca simulado como sucesso).
        if (!_executor.Disponivel)
        {
            throw new ExecutorBackupIndisponivelException();
        }

        // Dump lógico (consistência transacional é responsabilidade do executor — Fase 3).
        return await RealizarBackupAsync(
            tipo, tenantId, alvoLogico, periodicidade, (destino, ct) => _executor.GerarDumpAsync(alvoLogico, destino, ct), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ResultadoBackup> RealizarBackupAsync(
        TipoBackup tipo,
        Guid? tenantId,
        string alvoLogico,
        PeriodicidadeBackup periodicidade,
        Func<Stream, CancellationToken, Task> gerarConteudo,
        CancellationToken cancellationToken)
    {
        var registro = RegistroBackup.Iniciar(tipo, tenantId, alvoLogico, _relogio.AgoraUtc);
        await NoCatalogoAsync(async () =>
        {
            await _repositorio.AdicionarAsync(registro, cancellationToken).ConfigureAwait(false);
            await AuditarSemFilaAsync(AcaoAuditoria.BackupStarted, tipo, tenantId, registro.Id, null, ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        string hash;
        string local;
        string referencia;
        await using var artefato = await _temporarios.CriarAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 1. conteúdo (dump ou pacote de arquivos) num arquivo temporário privado — nunca a base inteira em memória.
            await using (var bruto = await _temporarios.CriarAsync(cancellationToken).ConfigureAwait(false))
            {
                await gerarConteudo(bruto, cancellationToken).ConfigureAwait(false);
                bruto.Position = 0;

                // 2. cifra em repouso (Fase 11) — chave do cofre por referência; nunca junto ao backup. Em blocos.
                if (_opcoes.CriptografarEmRepouso)
                {
                    await _cripto.CriptografarAsync(bruto, artefato, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await bruto.CopyToAsync(artefato, cancellationToken).ConfigureAwait(false);
                }
            }

            // 3. integridade SHA-256 sobre o ARTEFATO final (Fase 12) — nunca sobre o filename.
            artefato.Position = 0;
            hash = await HashBackup.CalcularAsync(artefato, cancellationToken).ConfigureAwait(false);
            var tamanho = artefato.Length;

            // 4. grava no store (failure domain separado — Fase 25) com nome versionado/timestamped (Fase 4).
            referencia = ComporReferencia(tipo, tenantId, registro.Id, _opcoes.CriptografarEmRepouso);
            artefato.Position = 0;
            local = await _store.GravarAsync(referencia, artefato, cancellationToken).ConfigureAwait(false);

            // 5. registra no catálogo de backups (Fase 13) — só metadados, nunca segredo.
            var retencaoAte = _retencao.CalcularRetencaoAte(periodicidade, _relogio.AgoraUtc);
            var hashFinal = hash;
            var localFinal = local;
            await NoCatalogoAsync(async () =>
            {
                registro.Concluir(hashFinal, tamanho, VersaoArtefato, localFinal, _opcoes.CriptografarEmRepouso,
                    _opcoes.CriptografarEmRepouso ? _cripto.ChaveRef : null, imutavel: false, retencaoAte, _relogio.AgoraUtc);
                await _repositorio.AtualizarAsync(registro, cancellationToken).ConfigureAwait(false);
                await AuditarSemFilaAsync(AcaoAuditoria.BackupCompleted, tipo, tenantId, registro.Id, hashFinal, ResultadoAuditoria.Permitido, cancellationToken)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelado ou TEMPO ESGOTADO do executor (o processo é encerrado): o registro nunca fica "Iniciado" — senão
            // a janela do dia contaria como atendida sem backup (JOB-001). Gravado mesmo com o pedido já cancelado.
            var motivo = cancellationToken.IsCancellationRequested ? "Cancelado" : "TempoEsgotado";
            await NoCatalogoAsync(async () =>
            {
                registro.Falhar(motivo, _relogio.AgoraUtc);
                await _repositorio.AtualizarAsync(registro, CancellationToken.None).ConfigureAwait(false);
                await AuditarSemFilaAsync(AcaoAuditoria.BackupFailed, tipo, tenantId, registro.Id, null, ResultadoAuditoria.Negado, CancellationToken.None)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await NoCatalogoAsync(async () =>
            {
                registro.Falhar(Sanitizar(ex), _relogio.AgoraUtc);
                await _repositorio.AtualizarAsync(registro, cancellationToken).ConfigureAwait(false);
                await AuditarSemFilaAsync(AcaoAuditoria.BackupFailed, tipo, tenantId, registro.Id, null, ResultadoAuditoria.Negado, cancellationToken)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
            throw;
        }

        // 6. cópia EXTERNA conferida pelo hash (BKP-003). O backup primário já vale; a falha da cópia fica registrada.
        var copia = await CopiarParaExternoAsync(registro, referencia, artefato, hash, cancellationToken).ConfigureAwait(false);
        return new ResultadoBackup(registro.Id, tipo, tenantId, StatusBackup.Concluido, hash, local, null, copia);
    }

    private async Task<SituacaoCopiaExterna?> CopiarParaExternoAsync(
        RegistroBackup registro, string referencia, Stream artefato, string hash, CancellationToken cancellationToken)
    {
        if (!_copiaExterna.Configurada)
        {
            return null;
        }

        bool sucesso;
        try
        {
            artefato.Position = 0;
            await _copiaExterna.CopiarAsync(referencia, artefato, hash, cancellationToken).ConfigureAwait(false);
            sucesso = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sucesso = false;
        }

        await NoCatalogoAsync(async () =>
        {
            registro.RegistrarCopiaExterna(sucesso, _relogio.AgoraUtc);
            await _repositorio.AtualizarAsync(registro, cancellationToken).ConfigureAwait(false);
            await AuditarSemFilaAsync(
                sucesso ? AcaoAuditoria.BackupCopiedOffsite : AcaoAuditoria.BackupOffsiteFailed,
                registro.Tipo, registro.TenantId, registro.Id, hash,
                sucesso ? ResultadoAuditoria.Permitido : ResultadoAuditoria.Negado,
                cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return registro.CopiaExterna;
    }

    private static string ComporReferencia(TipoBackup tipo, Guid? tenantId, Guid backupId, bool criptografado)
    {
        var alvo = tenantId?.ToString("N", CultureInfo.InvariantCulture) ?? "catalogo";
        var carimbo = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var tipoSlug = tipo.ToString().ToLowerInvariant();
        var sufixo = criptografado ? ".bak.enc" : ".bak";
        return $"{tipoSlug}/{alvo}/{carimbo}-{backupId:N}{sufixo}";
    }

    private static string Sanitizar(Exception ex)
    {
        // Nunca propaga connection string/segredo — usa o TIPO da exceção como motivo estável.
        return ex.GetType().Name;
    }

    /// <summary>
    /// Fila única de acesso ao catálogo neste escopo (ver <see cref="_travaCatalogo"/>): cada operação espera a anterior
    /// terminar — com sucesso ou falha — antes de começar. Sem objeto descartável; a fila nunca fica presa porque a
    /// conclusão de cada operação é sinalizada no <c>finally</c>.
    /// </summary>
    private async Task NoCatalogoAsync(Func<Task> operacao)
    {
        var minha = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task anterior;
        lock (_travaCatalogo)
        {
            anterior = _filaCatalogo;
            _filaCatalogo = minha.Task;
        }

        try
        {
            await anterior.ConfigureAwait(false);
            await operacao().ConfigureAwait(false);
        }
        finally
        {
            minha.SetResult();
        }
    }

    private async Task<T> NoCatalogoAsync<T>(Func<Task<T>> operacao)
    {
        T resultado = default!;
        // Corpo com chaves: é um Func<Task> (a forma "async () => x = await ..." seria Func<Task<T>> e chamaria a si mesma).
        await NoCatalogoAsync(async () => { resultado = await operacao().ConfigureAwait(false); }).ConfigureAwait(false);
        return resultado;
    }

    /// <summary>Grava o evento de auditoria. Só dentro de <see cref="NoCatalogoAsync(Func{Task})"/> (mesmo DbContext do catálogo).</summary>
    private ValueTask AuditarSemFilaAsync(
        string acao,
        TipoBackup tipo,
        Guid? tenantId,
        Guid backupId,
        string? hash,
        ResultadoAuditoria resultado,
        CancellationToken cancellationToken)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", tenantId?.ToString() ?? "GLOBAL", acao, resultado, _relogio.AgoraUtc)
            {
                TipoRecurso = "Backup",
                RecursoId = backupId.ToString(),
                Motivo = $"{tipo}",
                Protocolo = hash, // hash do artefato (não é segredo) — rastreabilidade de integridade
            },
            cancellationToken);
}
