using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Identidade;

/// <summary>
/// Administração de ACESSO (seção 21): conceder/revogar vínculo usuário×tenant e atribuir
/// papéis escopados. Toda operação é autorizada NO SERVIDOR (exige <c>admin.usuarios</c> no
/// tenant alvo) e bloqueia AUTO-CONCESSÃO: o próprio ator nunca concede privilégio a si mesmo
/// (nem papel, nem acesso). Revogação rotaciona a marca de segurança do alvo (seção 28).
/// </summary>
public sealed class GestaoAcesso
{
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IContextoSeguranca _seguranca;
    private readonly IRepositorioAcesso _acessos;
    private readonly IRepositorioUsuario _usuarios;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public GestaoAcesso(
        IServicoAutorizacao autorizacao,
        IContextoSeguranca seguranca,
        IRepositorioAcesso acessos,
        IRepositorioUsuario usuarios,
        IUnitOfWork unitOfWork,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(acessos);
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _autorizacao = autorizacao;
        _seguranca = seguranca;
        _acessos = acessos;
        _usuarios = usuarios;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    /// <summary>Concede a um usuário vínculo ativo com o tenant e um papel escopado.</summary>
    public async Task ConcederAcessoAsync(Guid tenantId, Guid usuarioAlvo, PapelConhecido papel, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(tenantId, Permissoes.AdminUsuarios, cancellationToken).ConfigureAwait(false);
        GarantirNaoEhOAtor(usuarioAlvo);

        var papelEntidade = await ResolverPapelAsync(papel, cancellationToken).ConfigureAwait(false);
        var agora = _relogio.AgoraUtc;

        var vinculo = await _acessos.ObterVinculoAsync(usuarioAlvo, tenantId, cancellationToken).ConfigureAwait(false);
        if (vinculo is null)
        {
            vinculo = UsuarioTenant.Conceder(usuarioAlvo, tenantId, agora);
            await _acessos.AdicionarVinculoAsync(vinculo, cancellationToken).ConfigureAwait(false);
        }

        await AtribuirPapelAoVinculoAsync(vinculo.Id, papelEntidade.Id, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, usuarioAlvo, "AcessoConcedido", ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atribui um papel a um vínculo existente e ativo.</summary>
    public async Task AtribuirPapelAsync(Guid tenantId, Guid usuarioAlvo, PapelConhecido papel, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(tenantId, Permissoes.AdminUsuarios, cancellationToken).ConfigureAwait(false);
        GarantirNaoEhOAtor(usuarioAlvo);

        var vinculo = await _acessos.ObterVinculoAsync(usuarioAlvo, tenantId, cancellationToken).ConfigureAwait(false);
        if (vinculo is null || !vinculo.EstaAtivo)
        {
            throw new AutorizacaoNegadaException("Vínculo inexistente ou inativo.");
        }

        var papelEntidade = await ResolverPapelAsync(papel, cancellationToken).ConfigureAwait(false);
        await AtribuirPapelAoVinculoAsync(vinculo.Id, papelEntidade.Id, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, usuarioAlvo, "PapelAtribuido", ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revoga o vínculo do usuário com o tenant e invalida as sessões dele (revogação).</summary>
    public async Task RevogarAcessoAsync(Guid tenantId, Guid usuarioAlvo, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(tenantId, Permissoes.AdminUsuarios, cancellationToken).ConfigureAwait(false);

        // Ninguém revoga o PRÓPRIO acesso (evita o contribuinte ficar sem administrador por engano).
        GarantirNaoEhOAtor(usuarioAlvo);

        var agora = _relogio.AgoraUtc;
        var vinculo = await _acessos.ObterVinculoAsync(usuarioAlvo, tenantId, cancellationToken).ConfigureAwait(false);
        if (vinculo is { } v && v.EstaAtivo)
        {
            v.Revogar(agora);
        }

        // Rotaciona a marca de segurança do alvo: sessões existentes perdem privilégio na
        // próxima revalidação (seção 28), mesmo que o circuito Blazor ainda esteja aberto.
        var usuario = await _usuarios.ObterPorIdAsync(usuarioAlvo, cancellationToken).ConfigureAwait(false);
        usuario?.RevogarSessoes(agora);

        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, usuarioAlvo, "AcessoRevogado", ResultadoAuditoria.Permitido, cancellationToken).ConfigureAwait(false);
    }

    private async Task AtribuirPapelAoVinculoAsync(Guid usuarioTenantId, Guid papelId, CancellationToken cancellationToken)
    {
        var jaTem = await _acessos.PapelDoVinculoExisteAsync(usuarioTenantId, papelId, cancellationToken).ConfigureAwait(false);
        if (!jaTem)
        {
            await _acessos.AdicionarPapelDoVinculoAsync(new UsuarioTenantPapel(usuarioTenantId, papelId), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Papel> ResolverPapelAsync(PapelConhecido papel, CancellationToken cancellationToken)
    {
        var entidade = await _acessos.ResolverPapelAsync(papel, cancellationToken).ConfigureAwait(false);
        return entidade ?? throw new AutorizacaoNegadaException("Papel inexistente no catálogo.");
    }

    private void GarantirNaoEhOAtor(Guid usuarioAlvo)
    {
        if (string.Equals(usuarioAlvo.ToString(), _seguranca.PrincipalId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AutoConcessaoNaoPermitidaException();
        }
    }

    private ValueTask AuditarAsync(Guid tenantId, Guid usuarioAlvo, string operacao, ResultadoAuditoria resultado, CancellationToken cancellationToken)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(
                _seguranca.PrincipalId ?? "sistema",
                tenantId.ToString(),
                $"{operacao}:{usuarioAlvo}",
                resultado,
                _relogio.AgoraUtc),
            cancellationToken);
}
