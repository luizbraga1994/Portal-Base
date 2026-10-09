using System.Collections.Frozen;
using PortalBase.Application.Autorizacao;
using PortalBase.Web.Seguranca;

namespace PortalBase.Web.Ui.Autorizacao;

/// <summary>
/// Resolve o conjunto EFETIVO de permissões do usuário autenticado para PROJEÇÃO VISUAL (Prompt 71,
/// F70-CRIT-01). Não é uma segunda fonte de verdade: apenas COMPÕE as fontes reais já existentes no
/// servidor — as permissões por tenant (<see cref="IRepositorioUsuario.ObterPermissoesAsync"/>, mesma
/// consulta que <see cref="IServicoAutorizacao"/> usa) e a autorização de plataforma
/// (<see cref="IAutorizacaoPlataforma"/>). Fail-closed: sem principal/tenant, ou em qualquer falha,
/// devolve conjunto vazio. A decisão REAL continua no servidor (handlers/casos de uso); esta projeção
/// é somente UX (mostrar/ocultar item de menu/botão).
/// </summary>
public interface IProvedorPermissoesVisuais
{
    Task<IReadOnlySet<string>> ResolverAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ProvedorPermissoesVisuais : IProvedorPermissoesVisuais
{
    private static readonly FrozenSet<string> Vazio = FrozenSet<string>.Empty;

    private readonly IContextoSeguranca _seguranca;
    private readonly IContextoTenantAtual _tenant;
    private readonly IRepositorioUsuario _usuarios;
    private readonly IAutorizacaoPlataforma _plataforma;

    public ProvedorPermissoesVisuais(
        IContextoSeguranca seguranca,
        IContextoTenantAtual tenant,
        IRepositorioUsuario usuarios,
        IAutorizacaoPlataforma plataforma)
    {
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(plataforma);
        _seguranca = seguranca;
        _tenant = tenant;
        _usuarios = usuarios;
        _plataforma = plataforma;
    }

    public async Task<IReadOnlySet<string>> ResolverAsync(CancellationToken cancellationToken = default)
    {
        // Fail-closed: sem principal autenticado válido não há projeção (só itens sem permissão exigida).
        if (!_seguranca.EstaAutenticado
            || string.IsNullOrWhiteSpace(_seguranca.PrincipalId)
            || !Guid.TryParse(_seguranca.PrincipalId, out var usuarioId))
        {
            return Vazio;
        }

        var conjunto = new HashSet<string>(StringComparer.Ordinal);

        // 1) Permissões efetivas NO TENANT corrente (vínculo ativo + papéis escopados). Só há tenant se a
        //    seleção foi re-validada no servidor (SelecaoTenant). MFA não entra aqui — é step-up de AÇÃO
        //    (a UI marca via IAutorizacaoVisual.ExigeMfa); ocultar a ação por falta de MFA seria incorreto.
        if (_tenant.TenantAtual is Guid tenantId && tenantId != Guid.Empty)
        {
            try
            {
                var permissoes = await _usuarios.ObterPermissoesAsync(usuarioId, tenantId, cancellationToken).ConfigureAwait(false);
                foreach (var chave in permissoes.Chaves)
                {
                    conjunto.Add(chave);
                }
            }
            catch (Exception excecao) when (excecao is not OperationCanceledException)
            {
                FalhasUi.Registrar(excecao);
                // Fail-closed: indisponibilidade da fonte NÃO concede permissão (deixa o bloco tenant fora).
            }
        }

        // 2) Permissões de PLATAFORMA (globais): concedidas em bloco se o principal for administrador de
        //    plataforma (allowlist do servidor). Reusa a autorização real; sem duplicar o allowlist na UI.
        try
        {
            var plataforma = await _plataforma.AvaliarAsync(Permissoes.SegurancaIncidentesGerir, cancellationToken).ConfigureAwait(false);
            if (plataforma.Permitido)
            {
                foreach (var chave in Permissoes.Plataforma)
                {
                    conjunto.Add(chave);
                }
            }
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            // Fail-closed.
        }

        return conjunto;
    }
}
