using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Multitenancy;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Contribuinte ativo do circuito, resolvido/validado no servidor (nunca do cliente). Abstração mínima
/// para consumidores que só precisam saber o tenant corrente (ex.: a projeção de permissões visuais),
/// sem depender do serviço concreto <see cref="SelecaoTenant"/> — facilita teste e evita acoplamento.
/// </summary>
public interface IContextoTenantAtual
{
    Guid? TenantAtual { get; }
}

/// <summary>
/// Seleção EXPLÍCITA do contribuinte ativo (seções 13, 14). A troca é sempre validada NO
/// SERVIDOR: <see cref="IResolvedorTenant"/> reinicia o contexto anterior e só autoriza o novo
/// tenant se o usuário atual tiver vínculo ativo — o valor vindo do navegador nunca é confiável.
/// Toda seleção/troca é AUDITADA (seção 21): sucesso registra o tenant anterior e o novo; a
/// negação registra a tentativa sem expor dados do tenant alvo. Escopo de circuito/requisição.
/// </summary>
public sealed class SelecaoTenant : IContextoTenantAtual
{
    private readonly IResolvedorTenant _resolvedor;
    private readonly IContextoSeguranca _seguranca;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public SelecaoTenant(
        IResolvedorTenant resolvedor,
        IContextoSeguranca seguranca,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(resolvedor);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);
        _resolvedor = resolvedor;
        _seguranca = seguranca;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    public Guid? TenantAtual { get; private set; }

    /// <summary>
    /// Troca para o tenant informado. Passo a passo (seção 14): invalida o contexto anterior,
    /// valida usuário/vínculo/tenant ativo no servidor e só então estabelece o novo. Se a
    /// validação falhar, a exceção propaga, NENHUM tenant fica selecionado (fail-closed) e a
    /// tentativa é auditada.
    /// </summary>
    public async Task SelecionarAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var anterior = TenantAtual;
        TenantAtual = null; // 1) invalida a seleção anterior antes de validar a nova
        var principal = _seguranca.PrincipalId ?? "anonimo";

        try
        {
            await _resolvedor.ResolverAsync(_seguranca.PrincipalId, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _auditoria.RegistrarAsync(
                new EventoAuditoria(principal, tenantId.ToString(), AcaoAuditoria.TenantAccessDenied, ResultadoAuditoria.Negado, _relogio.AgoraUtc)
                {
                    TipoRecurso = "Tenant",
                    RecursoId = tenantId.ToString(),
                },
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        TenantAtual = tenantId; // 7) só chega aqui se autorizado

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(principal, tenantId.ToString(), AcaoAuditoria.TenantContextSwitched, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = "Tenant",
                RecursoId = tenantId.ToString(),
                Metadados = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["tenantAnterior"] = anterior?.ToString(),
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// HIDRATA o contexto do tenant a partir da escolha JÁ PERSISTIDA na sessão (claim do cookie), no
    /// início do render do circuito. Diferente de <see cref="SelecionarAsync"/>: NÃO é uma troca do
    /// usuário — não emite auditoria de "troca" e NÃO propaga exceção (para não quebrar o render). Ainda
    /// assim é fail-closed: o vínculo é RE-VALIDADO no servidor (<see cref="IResolvedorTenant"/>) e, se
    /// falhar (vínculo revogado, tenant não operacional), nenhum tenant fica selecionado.
    /// </summary>
    public async Task HidratarAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (TenantAtual == tenantId)
        {
            return;
        }

        TenantAtual = null;
        if (tenantId == Guid.Empty)
        {
            return;
        }

        try
        {
            await _resolvedor.ResolverAsync(_seguranca.PrincipalId, tenantId, cancellationToken).ConfigureAwait(false);
            TenantAtual = tenantId;
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            TenantAtual = null; // fail-closed: sem tenant válido, as telas caem em SemTenant
        }
    }

    public void Limpar() => TenantAtual = null;
}
