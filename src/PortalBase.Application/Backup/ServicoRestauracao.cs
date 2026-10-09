using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Backup;

/// <summary>Resultado de uma restauração (Prompt 50) — metadados, nunca conteúdo/segredo.</summary>
public sealed record ResultadoRestauracao(Guid BackupId, Guid? TenantId, string BaseTemporaria, bool IntegridadeOk, bool Restaurado);

/// <summary>
/// SERVIÇO DE RESTAURAÇÃO (Prompt 50, Fases 14-16, 26, 28-30) — a metade que prova que BACKUP ≠ RESTORE.
/// Operação de PLATAFORMA altamente privilegiada (autorização de plataforma + MFA quando configurado;
/// nunca usuário comum do tenant — Fase 30). Sempre restaura em uma base TEMPORÁRIA (nunca sobrescreve
/// produção — Fase 14/15). Antes de restaurar: VERIFICA integridade por hash (Fase 12/26) e REJEITA
/// cross-tenant (backup de A só em A — Fase 29). Fail-closed em cada barreira; tudo auditado, sem segredo.
/// </summary>
public sealed class ServicoRestauracao
{
    private readonly IRepositorioRegistroBackup _repositorio;
    private readonly IArmazenamentoBackup _store;
    private readonly ICriptografiaBackup _cripto;
    private readonly IExecutorBackupBanco _executor;
    private readonly IProvedorAlvosBackup _alvos;
    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly IAuditoria _auditoria;
    private readonly IContextoSeguranca _seguranca;
    private readonly IRelogio _relogio;

    public ServicoRestauracao(
        IRepositorioRegistroBackup repositorio,
        IArmazenamentoBackup store,
        ICriptografiaBackup cripto,
        IExecutorBackupBanco executor,
        IProvedorAlvosBackup alvos,
        IAutorizacaoPlataforma autorizacao,
        IAuditoria auditoria,
        IContextoSeguranca seguranca,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cripto);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(alvos);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(relogio);

        _repositorio = repositorio;
        _store = store;
        _cripto = cripto;
        _executor = executor;
        _alvos = alvos;
        _autorizacao = autorizacao;
        _auditoria = auditoria;
        _seguranca = seguranca;
        _relogio = relogio;
    }

    /// <summary>
    /// Verifica a INTEGRIDADE de um backup (Fase 12/26): recalcula o hash do artefato e compara ao do
    /// catálogo. Divergência ⇒ marca CORROMPIDO, audita e devolve <c>false</c> (nunca restaurar depois).
    /// </summary>
    public async Task<bool> VerificarIntegridadeAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var registro = await CarregarRestauravelAsync(backupId, cancellationToken).ConfigureAwait(false);
        var ok = await ConferirHashAsync(registro, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            await MarcarCorrompidoAsync(registro, cancellationToken).ConfigureAwait(false);
        }

        return ok;
    }

    /// <summary>
    /// Restaura o backup de um TENANT em uma base TEMPORÁRIA (teste/promoção controlada). Autoriza
    /// plataforma; rejeita cross-tenant (Fase 29); nunca sobrescreve produção (Fase 15); verifica hash
    /// antes de restaurar (Fase 26). Registra o teste de restauração no catálogo (BACKUP ≠ RESTORE).
    /// </summary>
    public async Task<ResultadoRestauracao> RestaurarTenantParaTemporarioAsync(
        Guid backupId,
        Guid tenantIdDestino,
        string nomeBaseTemporaria,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeBaseTemporaria);
        await _autorizacao.ExigirAsync(Permissoes.RestauracaoExecutar, cancellationToken).ConfigureAwait(false);

        var registro = await CarregarRestauravelAsync(backupId, cancellationToken).ConfigureAwait(false);

        // Fase 29 — isolamento: o backup precisa ser de tenant E do MESMO tenant de destino.
        if (registro.Tipo == TipoBackup.CatalogoCompleto || registro.TenantId is null || registro.TenantId.Value != tenantIdDestino)
        {
            await AuditarAsync(AcaoAuditoria.RestoreCrossTenantDenied, tenantIdDestino, registro.Id, ResultadoAuditoria.Negado, cancellationToken).ConfigureAwait(false);
            throw new RestauracaoCrossTenantException();
        }

        // Fase 15 — nunca sobrescreve a base viva: o destino tem de ser uma base TEMPORÁRIA distinta.
        var alvo = await _alvos.ObterAsync(tenantIdDestino, cancellationToken).ConfigureAwait(false);
        if (alvo is not null && string.Equals(alvo.NomeBaseFisica, nomeBaseTemporaria, StringComparison.OrdinalIgnoreCase))
        {
            throw new RestauracaoEmProducaoException();
        }

        return await ExecutarRestauracaoAsync(registro, tenantIdDestino, nomeBaseTemporaria, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restaura o backup do CATÁLOGO em base temporária (Fase 14). Autoriza plataforma; nunca produção.</summary>
    public async Task<ResultadoRestauracao> RestaurarCatalogoParaTemporarioAsync(
        Guid backupId,
        string nomeBaseTemporaria,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeBaseTemporaria);
        await _autorizacao.ExigirAsync(Permissoes.RestauracaoExecutar, cancellationToken).ConfigureAwait(false);

        var registro = await CarregarRestauravelAsync(backupId, cancellationToken).ConfigureAwait(false);
        if (registro.Tipo != TipoBackup.CatalogoCompleto)
        {
            throw new BackupInvalidoException("O backup informado não é um backup de catálogo.");
        }

        if (string.Equals(registro.AlvoLogico, nomeBaseTemporaria, StringComparison.OrdinalIgnoreCase))
        {
            throw new RestauracaoEmProducaoException();
        }

        return await ExecutarRestauracaoAsync(registro, null, nomeBaseTemporaria, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResultadoRestauracao> ExecutarRestauracaoAsync(
        RegistroBackup registro,
        Guid? tenantId,
        string nomeBaseTemporaria,
        CancellationToken cancellationToken)
    {
        // Fase 33/28 — sem executor físico, a restauração real é BLOCKED (nunca marcada como sucesso).
        if (!_executor.Disponivel)
        {
            throw new ExecutorBackupIndisponivelException();
        }

        await AuditarAsync(AcaoAuditoria.RestoreStarted, tenantId, registro.Id, ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);

        try
        {
            // 1. integridade ANTES de restaurar (Fase 26) — corrupção ⇒ recusa.
            if (!await ConferirHashAsync(registro, cancellationToken).ConfigureAwait(false))
            {
                await MarcarCorrompidoAsync(registro, cancellationToken).ConfigureAwait(false);
                throw new BackupCorrompidoException();
            }

            // 2. abre o artefato, decifra se necessário (chave do cofre), restaura na base TEMPORÁRIA.
            await using var artefato = await _store.AbrirAsync(ReferenciaDe(registro), cancellationToken).ConfigureAwait(false);
            using var claro = new MemoryStream();
            if (registro.Criptografado)
            {
                await _cripto.DecriptografarAsync(artefato, claro, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await artefato.CopyToAsync(claro, cancellationToken).ConfigureAwait(false);
            }

            claro.Position = 0;
            await _executor.RestaurarEmBaseTemporariaAsync(nomeBaseTemporaria, claro, cancellationToken).ConfigureAwait(false);

            // 3. registra o TESTE de restauração no catálogo (BACKUP ≠ RESTORE).
            registro.RegistrarTesteRestauracao(sucesso: true, _relogio.AgoraUtc);
            await _repositorio.AtualizarAsync(registro, cancellationToken).ConfigureAwait(false);
            await AuditarAsync(AcaoAuditoria.RestoreTestCompleted, tenantId, registro.Id, ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);
            await AuditarAsync(AcaoAuditoria.RestoreCompleted, tenantId, registro.Id, ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);

            return new ResultadoRestauracao(registro.Id, tenantId, nomeBaseTemporaria, IntegridadeOk: true, Restaurado: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BackupCorrompidoException)
        {
            await AuditarAsync(AcaoAuditoria.RestoreFailed, tenantId, registro.Id, ResultadoAuditoria.Negado, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<RegistroBackup> CarregarRestauravelAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var registro = await _repositorio.ObterAsync(backupId, cancellationToken).ConfigureAwait(false)
            ?? throw new BackupInvalidoException("Backup não encontrado no catálogo.");

        // Fase 27 — só um backup concluído com hash é candidato; incompleto/corrompido/expirado ⇒ recusa.
        if (!registro.CandidatoRestauracao)
        {
            throw new BackupInvalidoException($"Backup em estado não restaurável: '{registro.Status}'.");
        }

        return registro;
    }

    private async Task<bool> ConferirHashAsync(RegistroBackup registro, CancellationToken cancellationToken)
    {
        if (!await _store.ExisteAsync(ReferenciaDe(registro), cancellationToken).ConfigureAwait(false))
        {
            return false; // artefato ausente ⇒ tratado como falha de integridade (Fase 23/28)
        }

        await using var artefato = await _store.AbrirAsync(ReferenciaDe(registro), cancellationToken).ConfigureAwait(false);
        var hash = await HashBackup.CalcularAsync(artefato, cancellationToken).ConfigureAwait(false);
        return HashBackup.Conferem(registro.HashSha256, hash);
    }

    private async Task MarcarCorrompidoAsync(RegistroBackup registro, CancellationToken cancellationToken)
    {
        registro.MarcarCorrompido("Hash de integridade divergente na verificação.", _relogio.AgoraUtc);
        await _repositorio.AtualizarAsync(registro, cancellationToken).ConfigureAwait(false);
        await AuditarAsync(AcaoAuditoria.BackupCorrupted, registro.TenantId, registro.Id, ResultadoAuditoria.Negado, cancellationToken).ConfigureAwait(false);
    }

    // A referência lógica do artefato é o local registrado no catálogo (nunca um caminho com credencial).
    private static string ReferenciaDe(RegistroBackup registro)
        => registro.LocalArmazenamentoLogico ?? throw new BackupInvalidoException("Registro de backup sem local de armazenamento.");

    private ValueTask AuditarAsync(string acao, Guid? tenantId, Guid backupId, ResultadoAuditoria resultado, CancellationToken cancellationToken)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", tenantId?.ToString() ?? "GLOBAL", acao, resultado, _relogio.AgoraUtc)
            {
                TipoRecurso = "Backup",
                RecursoId = backupId.ToString(),
            },
            cancellationToken);
}
