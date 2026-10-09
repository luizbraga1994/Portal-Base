using System.Globalization;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Provisionamento;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Backup;

/// <summary>Resultado do TESTE de restauração (restaurado numa base temporária, conferido e removido).</summary>
public sealed record ResultadoTesteRestauracao(Guid BackupId, bool Restaurado, bool BaseTemporariaRemovida);

/// <summary>Resultado da restauração APLICADA na base do contribuinte (sem nome de base nem detalhe técnico).</summary>
public sealed record ResultadoRestauracaoAplicada(Guid BackupId, Guid TenantId, string SchemaVersion, bool ContribuinteReativado);

/// <summary>
/// RESTAURAÇÃO pela tela da plataforma (Administrador da plataforma; <c>plataforma.restore.executar</c>, com MFA para
/// quem é administrador só pela configuração). Duas operações:
/// <list type="bullet">
/// <item><b>Testar</b> — restaura o backup (catálogo ou contribuinte) numa base TEMPORÁRIA derivada da base de origem
/// (<c>{base}_tst{carimbo}{4 hex}</c> — cabe nos grants da conta admin por prefixo), confere o dono da base e a remove;
/// marca o backup como "restauração testada". Não toca em produção.</item>
/// <item><b>Restaurar contribuinte</b> — nunca sobrescreve sem volta: (1) só backup CONCLUÍDO com hash, do MESMO
/// contribuinte e da MESMA base física (cross-tenant ⇒ negado e auditado); (2) restaura numa base de PREPARO e confere a
/// marca de proprietário; (3) coloca o contribuinte em manutenção (desabilitado) se estava ativo; (4) TROCA ATÔMICA das
/// tabelas — as atuais vão para uma base PRESERVADA, as do backup entram na produção; (5) aplica as migrations (backup
/// antigo ⇒ schema atualizado); (6) reativa. Falha na troca ⇒ nada muda e o contribuinte volta ao estado anterior;
/// falha nas migrations ⇒ o contribuinte fica desabilitado (fail-closed). Um por vez por contribuinte (bloqueio no banco).</item>
/// </list>
/// O catálogo não é restaurado pela tela (o portal roda sobre ele): só testado. Tudo auditado, sem segredo.
/// </summary>
public sealed class RestauracaoPlataforma
{
    public const int JustificativaMinima = 10;
    public const int JustificativaMaxima = 400;

    private readonly ServicoRestauracao _restauracao;
    private readonly IRepositorioRegistroBackup _registros;
    private readonly IProvedorAlvosBackup _alvos;
    private readonly IAdministradorBancoTenant _bancos;
    private readonly IOperacoesBaseRestauracao _operacoes;
    private readonly IMigradorTenant _migrador;
    private readonly IRepositorioTenant _tenants;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly IAuditoria _auditoria;
    private readonly IContextoSeguranca _seguranca;
    private readonly IRelogio _relogio;

    public RestauracaoPlataforma(
        ServicoRestauracao restauracao,
        IRepositorioRegistroBackup registros,
        IProvedorAlvosBackup alvos,
        IAdministradorBancoTenant bancos,
        IOperacoesBaseRestauracao operacoes,
        IMigradorTenant migrador,
        IRepositorioTenant tenants,
        IUnitOfWork unitOfWork,
        IAutorizacaoPlataforma autorizacao,
        IAuditoria auditoria,
        IContextoSeguranca seguranca,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(restauracao);
        ArgumentNullException.ThrowIfNull(registros);
        ArgumentNullException.ThrowIfNull(alvos);
        ArgumentNullException.ThrowIfNull(bancos);
        ArgumentNullException.ThrowIfNull(operacoes);
        ArgumentNullException.ThrowIfNull(migrador);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(relogio);
        _restauracao = restauracao;
        _registros = registros;
        _alvos = alvos;
        _bancos = bancos;
        _operacoes = operacoes;
        _migrador = migrador;
        _tenants = tenants;
        _unitOfWork = unitOfWork;
        _autorizacao = autorizacao;
        _auditoria = auditoria;
        _seguranca = seguranca;
        _relogio = relogio;
    }

    /// <summary>Restaura numa base TEMPORÁRIA, confere e remove a base. Produção nunca é tocada.</summary>
    public async Task<ResultadoTesteRestauracao> TestarAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.RestauracaoExecutar, cancellationToken).ConfigureAwait(false);
        var registro = await _registros.ObterAsync(backupId, cancellationToken).ConfigureAwait(false)
            ?? throw new BackupInvalidoException("Backup não encontrado no catálogo.");

        var sufixo = _relogio.AgoraUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..4];
        var temporaria = NomeDerivado(registro.AlvoLogico, "tst", sufixo);
        if (await _bancos.BancoExisteAsync(temporaria, cancellationToken).ConfigureAwait(false))
        {
            // Nunca reaproveita (nem apaga) uma base que não foi criada por este teste.
            throw new OperacaoBackupEmAndamentoException("Já existe um teste de restauração deste backup neste instante. Tente de novo em um minuto.");
        }

        var removida = false;
        try
        {
            if (registro.Tipo == TipoBackup.CatalogoCompleto)
            {
                await _restauracao.RestaurarCatalogoParaTemporarioAsync(backupId, temporaria, cancellationToken).ConfigureAwait(false);
            }
            else if (registro.TenantId is Guid tenantId)
            {
                await _restauracao.RestaurarTenantParaTemporarioAsync(backupId, tenantId, temporaria, cancellationToken).ConfigureAwait(false);
                await ConferirDonoAsync(temporaria, tenantId, backupId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new BackupInvalidoException("Backup sem contribuinte de origem.");
            }
        }
        finally
        {
            removida = await RemoverSeguroAsync(temporaria).ConfigureAwait(false);
        }

        return new ResultadoTesteRestauracao(backupId, Restaurado: true, removida);
    }

    /// <summary>Restaura o backup SOBRE a base do contribuinte (troca atômica; a base atual fica preservada).</summary>
    /// <exception cref="EntradaInvalidaException">Justificativa ausente/curta.</exception>
    /// <exception cref="RestauracaoCrossTenantException">Backup de outro contribuinte ou de outra base.</exception>
    /// <exception cref="OperacaoBackupEmAndamentoException">Outra restauração do mesmo contribuinte em andamento.</exception>
    /// <exception cref="MigracaoAposRestauracaoException">Dados restaurados, mas o schema não foi atualizado.</exception>
    public async Task<ResultadoRestauracaoAplicada> RestaurarContribuinteAsync(
        Guid backupId, Guid tenantId, string? justificativa, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.RestauracaoExecutar, cancellationToken).ConfigureAwait(false);
        var motivo = (justificativa ?? string.Empty).Trim();
        if (motivo.Length < JustificativaMinima)
        {
            throw new EntradaInvalidaException($"Explique a restauração (mínimo de {JustificativaMinima} caracteres) — fica registrado na auditoria.");
        }

        motivo = motivo.Length > JustificativaMaxima ? motivo[..JustificativaMaxima] : motivo;

        var registro = await _registros.ObterAsync(backupId, cancellationToken).ConfigureAwait(false)
            ?? throw new BackupInvalidoException("Backup não encontrado no catálogo.");
        if (!registro.CandidatoRestauracao)
        {
            throw new BackupInvalidoException("Este backup não pode ser restaurado (não concluído, corrompido ou expirado).");
        }

        var alvo = await _alvos.ObterAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (registro.Tipo != TipoBackup.TenantCompleto || registro.TenantId != tenantId || alvo is null
            || !string.Equals(registro.AlvoLogico, alvo.NomeBaseFisica, StringComparison.OrdinalIgnoreCase))
        {
            // Isolamento: só o backup DESTE contribuinte, tirado DESTA base, volta para ela.
            await AuditarAsync(AcaoAuditoria.RestoreCrossTenantDenied, tenantId, backupId, ResultadoAuditoria.Negado, motivo, null, cancellationToken).ConfigureAwait(false);
            throw new RestauracaoCrossTenantException();
        }

        if (alvo.Estado is not (EstadoProvisionamento.Pronto or EstadoProvisionamento.Ativo or EstadoProvisionamento.Desabilitado))
        {
            throw new BackupInvalidoException("O contribuinte não está provisionado (sem base pronta para receber a restauração).");
        }

        await using var bloqueio = await _operacoes.BloquearAsync($"portalbase_rest_{tenantId:N}", cancellationToken).ConfigureAwait(false);

        var producao = alvo.NomeBaseFisica;
        var carimbo = _relogio.AgoraUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var preparo = NomeDerivado(producao, "rst", carimbo);
        var preservada = NomeDerivado(producao, "ant", carimbo);
        if (await _bancos.BancoExisteAsync(preparo, cancellationToken).ConfigureAwait(false)
            || await _bancos.BancoExisteAsync(preservada, cancellationToken).ConfigureAwait(false))
        {
            throw new OperacaoBackupEmAndamentoException("Já existe uma restauração deste contribuinte neste instante. Tente de novo em um minuto.");
        }

        // 1. Restaura na base de PREPARO (hash conferido antes; produção intacta).
        try
        {
            await _restauracao.RestaurarTenantParaTemporarioAsync(backupId, tenantId, preparo, cancellationToken).ConfigureAwait(false);
            await ConferirDonoAsync(preparo, tenantId, backupId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RemoverSeguroAsync(preparo).ConfigureAwait(false);
            throw;
        }

        // 2. Manutenção: ninguém grava durante a troca.
        var tenant = await _tenants.ObterPorIdAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new BackupInvalidoException("Contribuinte não encontrado no catálogo.");
        var estavaAtivo = tenant.Estado == EstadoProvisionamento.Ativo;
        if (estavaAtivo)
        {
            tenant.Desabilitar(_relogio.AgoraUtc);
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3. Troca ATÔMICA das tabelas (falhou ⇒ nada mudou: volta ao estado anterior).
        try
        {
            await _operacoes.TrocarTabelasAsync(producao, preparo, preservada, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RemoverSeguroAsync(preparo).ConfigureAwait(false);
            if (estavaAtivo)
            {
                tenant.Reativar(_relogio.AgoraUtc);
                await _unitOfWork.SalvarAlteracoesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await AuditarAsync(AcaoAuditoria.RestoreApplyFailed, tenantId, backupId, ResultadoAuditoria.Negado, motivo,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["etapa"] = "troca", ["erro"] = ex.GetType().Name }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // 4. Schema atualizado (o backup pode ser de antes de uma migration).
        ResultadoMigracao migracao;
        try
        {
            migracao = await _migrador.AplicarMigrationsAsync(producao, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            tenant.RegistrarFalhaMigracao($"Restauração aplicada; migrations falharam ({ex.GetType().Name}).", _relogio.AgoraUtc);
            await _unitOfWork.SalvarAlteracoesAsync(CancellationToken.None).ConfigureAwait(false);
            await AuditarAsync(AcaoAuditoria.RestoreApplyFailed, tenantId, backupId, ResultadoAuditoria.Negado, motivo,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["etapa"] = "migrations",
                    ["erro"] = ex.GetType().Name,
                    ["basePreservada"] = preservada,
                }, CancellationToken.None).ConfigureAwait(false);
            throw new MigracaoAposRestauracaoException();
        }

        // 5. Volta a operar (se estava ativo) e registra.
        tenant.RegistrarVersaoSchema(migracao.SchemaVersion, migracao.UltimaMigracao, _relogio.AgoraUtc);
        if (estavaAtivo)
        {
            tenant.Reativar(_relogio.AgoraUtc);
        }

        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(AcaoAuditoria.RestoreAppliedToTenant, tenantId, backupId, ResultadoAuditoria.Permitido, motivo,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["basePreservada"] = preservada,
                ["schema"] = migracao.SchemaVersion,
                ["backupDe"] = registro.IniciadoEmUtc.ToString("O", CultureInfo.InvariantCulture),
            }, cancellationToken).ConfigureAwait(false);

        return new ResultadoRestauracaoAplicada(backupId, tenantId, migracao.SchemaVersion, estavaAtivo);
    }

    /// <summary>Nome derivado da base de produção (≤ 64, minúsculo): <c>{base}_{marca}{carimbo}</c>.</summary>
    internal static string NomeDerivado(string producao, string marca, string carimbo)
    {
        var sufixo = $"_{marca}{carimbo}";
        var prefixo = producao.Length > 64 - sufixo.Length ? producao[..(64 - sufixo.Length)] : producao;
        return (prefixo + sufixo).ToLowerInvariant();
    }

    // Marca de proprietário da base restaurada: com dono gravado, tem de ser o mesmo contribuinte; vazia ⇒ inválida.
    private async Task ConferirDonoAsync(string nomeBase, Guid tenantId, Guid backupId, CancellationToken cancellationToken)
    {
        var dono = await _bancos.ObterProprietarioAsync(nomeBase, cancellationToken).ConfigureAwait(false);
        if (dono.Vazia)
        {
            throw new BackupInvalidoException("O backup restaurado está vazio.");
        }

        if (dono.TenantId is Guid outro && outro != tenantId)
        {
            await AuditarAsync(AcaoAuditoria.RestoreCrossTenantDenied, tenantId, backupId, ResultadoAuditoria.Negado, "marca de proprietário divergente", null, cancellationToken)
                .ConfigureAwait(false);
            throw new RestauracaoCrossTenantException();
        }
    }

    // Remove uma base TEMPORÁRIA/de preparo gerada aqui (nunca a de produção). Falha não esconde o erro principal.
    private async Task<bool> RemoverSeguroAsync(string nomeBase)
    {
        try
        {
            if (await _bancos.BancoExisteAsync(nomeBase, CancellationToken.None).ConfigureAwait(false))
            {
                await _bancos.RemoverBancoAsync(nomeBase, CancellationToken.None).ConfigureAwait(false);
            }

            return true;
        }
#pragma warning disable CA1031 // Limpeza best-effort: a base órfã é só de preparo; o erro relevante é o da operação.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private ValueTask AuditarAsync(
        string acao, Guid tenantId, Guid backupId, ResultadoAuditoria resultado, string motivo,
        IReadOnlyDictionary<string, string?>? metadados, CancellationToken cancellationToken)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", tenantId.ToString(), acao, resultado, _relogio.AgoraUtc)
            {
                TipoRecurso = "Backup",
                RecursoId = backupId.ToString(),
                Motivo = motivo,
                Metadados = metadados,
            },
            cancellationToken);
}
