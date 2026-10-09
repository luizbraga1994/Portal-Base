using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Provisionamento;

/// <summary>Desfecho de uma operação de ciclo de vida de tenant.</summary>
public sealed record ResultadoCicloVida(Guid TenantId, EstadoProvisionamento Estado, string? Motivo)
{
    public bool Sucesso => Motivo is null;
}

/// <summary>
/// Ciclo de vida OPERACIONAL do tenant (Prompt 09, seções 3, 4, 20, 21, 22): ativação
/// (Ready→Active), desabilitação (Active→Disabled) e reativação (Disabled→Active). Toda
/// operação é AUTORIZADA no servidor (permissão administrativa <c>admin.tenant</c> no tenant,
/// deny-by-default), AUDITADA, tenant-scoped e fail-closed. Ativação e reativação VALIDAM a
/// infraestrutura antes de tornar o tenant operacional ("Ready/Active" nunca é presumido). A
/// desabilitação NUNCA remove a base (seção 23). As transições são server-side — o cliente
/// nunca envia <c>status=Active</c>.
/// </summary>
public sealed class CicloVidaTenant
{
    private readonly IRepositorioTenant _repositorio;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IValidadorProvisionamento _validador;
    private readonly IAuditoria _auditoria;
    private readonly IContextoSeguranca _seguranca;
    private readonly IRelogio _relogio;

    public CicloVidaTenant(
        IRepositorioTenant repositorio,
        IUnitOfWork unitOfWork,
        IServicoAutorizacao autorizacao,
        IValidadorProvisionamento validador,
        IAuditoria auditoria,
        IContextoSeguranca seguranca,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(validador);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(relogio);

        _repositorio = repositorio;
        _unitOfWork = unitOfWork;
        _autorizacao = autorizacao;
        _validador = validador;
        _auditoria = auditoria;
        _seguranca = seguranca;
        _relogio = relogio;
    }

    /// <summary>Ativa um tenant PRONTO (Ready→Active) após validar a infraestrutura.</summary>
    public async Task<ResultadoCicloVida> AtivarAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await AutorizarECarregarAsync(tenantId, cancellationToken).ConfigureAwait(false);

        if (tenant.Estado == EstadoProvisionamento.Ativo)
        {
            return new ResultadoCicloVida(tenantId, tenant.Estado, null); // idempotente
        }

        if (tenant.Estado != EstadoProvisionamento.Pronto)
        {
            return new ResultadoCicloVida(tenantId, tenant.Estado, "Tenant não está pronto para ativação.");
        }

        var validacao = await _validador.ValidarAsync(tenant.NomeBaseFisica, cancellationToken).ConfigureAwait(false);
        if (!validacao.Ok)
        {
            return new ResultadoCicloVida(tenantId, tenant.Estado, validacao.Motivo ?? "Infraestrutura inválida.");
        }

        tenant.Ativar(_relogio.AgoraUtc);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.TenantActivated, ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);

        return new ResultadoCicloVida(tenantId, tenant.Estado, null);
    }

    /// <summary>Desabilita um tenant (Active/Ready→Disabled). NÃO remove a base (seção 23).</summary>
    public async Task<ResultadoCicloVida> DesabilitarAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await AutorizarECarregarAsync(tenantId, cancellationToken).ConfigureAwait(false);

        if (tenant.Estado == EstadoProvisionamento.Desabilitado)
        {
            return new ResultadoCicloVida(tenantId, tenant.Estado, null); // idempotente
        }

        tenant.Desabilitar(_relogio.AgoraUtc);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.TenantDisabled, ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);

        return new ResultadoCicloVida(tenantId, tenant.Estado, null);
    }

    /// <summary>Reativa um tenant DESABILITADO (Disabled→Active) após revalidar a infraestrutura.</summary>
    public async Task<ResultadoCicloVida> ReativarAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await AutorizarECarregarAsync(tenantId, cancellationToken).ConfigureAwait(false);

        if (tenant.Estado == EstadoProvisionamento.Ativo)
        {
            return new ResultadoCicloVida(tenantId, tenant.Estado, null); // idempotente
        }

        if (tenant.Estado != EstadoProvisionamento.Desabilitado)
        {
            return new ResultadoCicloVida(tenantId, tenant.Estado, "Só um tenant desabilitado pode ser reativado.");
        }

        var validacao = await _validador.ValidarAsync(tenant.NomeBaseFisica, cancellationToken).ConfigureAwait(false);
        if (!validacao.Ok)
        {
            return new ResultadoCicloVida(tenantId, tenant.Estado, validacao.Motivo ?? "Infraestrutura inválida.");
        }

        tenant.Reativar(_relogio.AgoraUtc);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.TenantReactivated, ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);

        return new ResultadoCicloVida(tenantId, tenant.Estado, null);
    }

    // Autorização ADMINISTRATIVA (não exige tenant operacional) + carga do agregado (fail-closed).
    private async Task<Tenant> AutorizarECarregarAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await _autorizacao.ExigirAdministrativoAsync(tenantId, Permissoes.AdminTenant, cancellationToken).ConfigureAwait(false);

        return await _repositorio.ObterPorIdAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantNaoEncontradoException();
    }

    private async Task AuditarAsync(Guid tenantId, string operacao, ResultadoAuditoria resultado, CancellationToken cancellationToken)
    {
        var principal = _seguranca.PrincipalId ?? "sistema";
        await _auditoria
            .RegistrarAsync(
                new EventoAuditoria(principal, tenantId.ToString(), operacao, resultado, _relogio.AgoraUtc)
                {
                    TipoRecurso = "Tenant",
                    RecursoId = tenantId.ToString(),
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
