using Microsoft.Extensions.Logging;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Provisionamento;

/// <summary>
/// Orquestra o provisionamento/atualização de um tenant (seção 12), idempotente e
/// fail-closed: cria a base (se ausente), aplica migrations, concede grants de runtime,
/// valida conectividade e registra a versão de schema. Concorrência via lock (seção 24).
/// Em falha, o tenant NÃO fica Pronto; o erro é sanitizado (sem segredos/connection string).
/// </summary>
public sealed partial class ProvisionadorTenant : IProvisionadorTenant
{
    private readonly IRepositorioTenant _repositorio;
    private readonly IAdministradorBancoTenant _admin;
    private readonly IMigradorTenant _migrador;
    private readonly IValidadorProvisionamento _validador;
    private readonly ILockProvisionamento _lock;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IMetricasProvisionamento _metricas;
    private readonly IRelogio _relogio;
    private readonly ILogger<ProvisionadorTenant> _logger;

    public ProvisionadorTenant(
        IRepositorioTenant repositorio,
        IAdministradorBancoTenant admin,
        IMigradorTenant migrador,
        IValidadorProvisionamento validador,
        ILockProvisionamento @lock,
        IUnitOfWork unitOfWork,
        IAuditoria auditoria,
        IMetricasProvisionamento metricas,
        IRelogio relogio,
        ILogger<ProvisionadorTenant> logger)
    {
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(migrador);
        ArgumentNullException.ThrowIfNull(validador);
        ArgumentNullException.ThrowIfNull(@lock);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(metricas);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(logger);

        _repositorio = repositorio;
        _admin = admin;
        _migrador = migrador;
        _validador = validador;
        _lock = @lock;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _metricas = metricas;
        _relogio = relogio;
        _logger = logger;
    }

    public async ValueTask<ResultadoProvisionamento> ProvisionarAsync(
        Guid tenantId,
        string solicitanteId,
        CancellationToken cancellationToken = default)
    {
        // Seção 24: exclusão mútua. Se outro processo provisiona, não iniciar concorrente.
        await using var trava = await _lock.TentarAdquirirAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (trava is null)
        {
            return new ResultadoProvisionamento(
                tenantId, EstadoProvisionamento.Provisionando, null, "Provisionamento concorrente em andamento.");
        }

        var tenant = await _repositorio.ObterPorIdAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantNaoEncontradoException();

        if (tenant.Estado == EstadoProvisionamento.Desabilitado)
        {
            return new ResultadoProvisionamento(tenantId, tenant.Estado, tenant.SchemaVersion, "Tenant desabilitado.");
        }

        // Atualização de schema é para tenants JÁ provisionados (Pronto/Ready ou Ativo/Active) —
        // preserva o estado operacional; o provisionamento inicial parte de Pendente/Falhado.
        var atualizacaoDeSchema = tenant.EstaProvisionado;
        var nomeBase = tenant.NomeBaseFisica;

        if (!atualizacaoDeSchema)
        {
            tenant.IniciarProvisionamento(_relogio.AgoraUtc);
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            await AuditarAsync(solicitanteId, tenantId, AcaoAuditoria.TenantProvisioningStarted, ResultadoAuditoria.Permitido, cancellationToken)
                .ConfigureAwait(false);
        }

        _metricas.RegistrarInicio();
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Cria a base apenas se ausente (idempotente); audita a criação só quando ocorre.
            if (!await _admin.BancoExisteAsync(nomeBase, cancellationToken).ConfigureAwait(false))
            {
                await _admin.CriarBancoSeAusenteAsync(nomeBase, cancellationToken).ConfigureAwait(false);
                await AuditarAsync(solicitanteId, tenantId, AcaoAuditoria.TenantDatabaseCreated, ResultadoAuditoria.Permitido, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!atualizacaoDeSchema)
            {
                // Nome escolhido ⇒ a base pode existir ANTES do tenant: nunca herdar dados de outro contribuinte.
                var dono = await _admin.ObterProprietarioAsync(nomeBase, cancellationToken).ConfigureAwait(false);
                if (!ReaproveitamentoBaseTenant.Permitido(dono, tenantId, tenant.Cnpj, nomeBase))
                {
                    await AuditarAsync(solicitanteId, tenantId, AcaoAuditoria.TenantDatabaseOwnershipConflict, ResultadoAuditoria.Negado, cancellationToken)
                        .ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "A base física deste nome já existe com dados de outro contribuinte (ou sem dono conhecido); escolha outro nome de tenant.");
                }

                if (dono.TenantId != tenantId)
                {
                    await AuditarAsync(solicitanteId, tenantId, AcaoAuditoria.TenantDatabaseReused, ResultadoAuditoria.Permitido, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // Marca de proprietário ANTES das migrations: uma nova tentativa reconhece a própria base.
            if (!atualizacaoDeSchema)
            {
                await _admin.RegistrarProprietarioAsync(nomeBase, tenantId, tenant.Cnpj.Valor, cancellationToken).ConfigureAwait(false);
            }

            await AuditarAsync(solicitanteId, tenantId, AcaoAuditoria.TenantDatabaseMigrationStarted, ResultadoAuditoria.Permitido, cancellationToken)
                .ConfigureAwait(false);
            var cronometroMigracao = System.Diagnostics.Stopwatch.StartNew();
            var migracao = await _migrador.AplicarMigrationsAsync(nomeBase, cancellationToken).ConfigureAwait(false);
            cronometroMigracao.Stop();
            _metricas.RegistrarDuracaoMigracao(cronometroMigracao.Elapsed);
            await AuditarAsync(solicitanteId, tenantId, AcaoAuditoria.TenantDatabaseMigrationCompleted, ResultadoAuditoria.Permitido, cancellationToken)
                .ConfigureAwait(false);

            await _admin.ConcederAcessoRuntimeAsync(nomeBase, cancellationToken).ConfigureAwait(false);

            // "Ready" só depois de VALIDAR: conectividade de runtime + tabelas essenciais + sem
            // migrations pendentes (seção 15). Falha em qualquer etapa ⇒ Failed (não Ready parcial).
            var validacao = await _validador.ValidarAsync(nomeBase, cancellationToken).ConfigureAwait(false);
            if (!validacao.Ok)
            {
                throw new InvalidOperationException(validacao.Motivo ?? "Validação pós-provisionamento falhou.");
            }

            if (atualizacaoDeSchema)
            {
                tenant.RegistrarVersaoSchema(migracao.SchemaVersion, migracao.UltimaMigracao, _relogio.AgoraUtc);
            }
            else
            {
                tenant.ConcluirProvisionamento(migracao.SchemaVersion, migracao.UltimaMigracao, _relogio.AgoraUtc);
            }

            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            await AuditarAsync(solicitanteId, tenantId, AcaoAuditoria.TenantProvisioningCompleted, ResultadoAuditoria.Permitido, cancellationToken)
                .ConfigureAwait(false);

            cronometro.Stop();
            _metricas.RegistrarSucesso(cronometro.Elapsed);
            return new ResultadoProvisionamento(tenantId, tenant.Estado, tenant.SchemaVersion, null);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            var erroSanitizado = Sanitizar(excecao);
            LogFalhaProvisionamento(_logger, excecao, tenantId);

            if (atualizacaoDeSchema)
            {
                tenant.RegistrarFalhaMigracao(erroSanitizado, _relogio.AgoraUtc);
            }
            else
            {
                tenant.FalharProvisionamento(erroSanitizado, _relogio.AgoraUtc);
            }

            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            await AuditarAsync(solicitanteId, tenantId, AcaoAuditoria.TenantProvisioningFailed, ResultadoAuditoria.Negado, cancellationToken)
                .ConfigureAwait(false);

            cronometro.Stop();
            _metricas.RegistrarFalha(cronometro.Elapsed);
            return new ResultadoProvisionamento(tenantId, tenant.Estado, tenant.SchemaVersion, erroSanitizado);
        }
    }

    private async Task AuditarAsync(
        string solicitanteId,
        Guid tenantId,
        string operacao,
        ResultadoAuditoria resultado,
        CancellationToken cancellationToken)
    {
        var principal = string.IsNullOrWhiteSpace(solicitanteId) ? "sistema" : solicitanteId;
        await _auditoria
            .RegistrarAsync(new EventoAuditoria(principal, tenantId.ToString(), operacao, resultado, _relogio.AgoraUtc), cancellationToken)
            .ConfigureAwait(false);
    }

    // Mensagem segura para persistir/auditar: tipo + texto curto, SEM connection string/segredo.
    private static string Sanitizar(Exception excecao)
    {
        var texto = excecao.Message;
        var quebra = texto.IndexOf('\n', StringComparison.Ordinal);
        if (quebra >= 0)
        {
            texto = texto[..quebra];
        }

        return $"{excecao.GetType().Name}: {texto}".Trim();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha de provisionamento do tenant {TenantId}.")]
    private static partial void LogFalhaProvisionamento(ILogger logger, Exception excecao, Guid tenantId);
}
