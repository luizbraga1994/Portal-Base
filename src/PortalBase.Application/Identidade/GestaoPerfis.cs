using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Identidade;

/// <summary>
/// CADASTRO de perfis do contribuinte: listar (do sistema + personalizados), cadastrar, alterar (nome, descrição e
/// QUAIS permissões o perfil dá) e excluir. Os perfis do sistema (Leitor, Analista, Administrador) valem
/// para todos os contribuintes e não mudam por aqui — servem de modelo. Exige <c>admin.usuarios</c> no contribuinte da
/// sessão; só entram permissões de CONTRIBUINTE (as de plataforma são do Administrador da plataforma); ninguém põe num
/// perfil uma permissão que não tem (anti-escalada). Tudo auditado.
/// </summary>
public sealed class GestaoPerfis
{
    private const int TamanhoMinimoNome = 2;

    private readonly IServicoAutorizacao _autorizacao;
    private readonly IContextoSeguranca _seguranca;
    private readonly IRepositorioGestaoUsuarios _repositorio;
    private readonly IRepositorioUsuario _usuarios;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public GestaoPerfis(
        IServicoAutorizacao autorizacao,
        IContextoSeguranca seguranca,
        IRepositorioGestaoUsuarios repositorio,
        IRepositorioUsuario usuarios,
        IUnitOfWork unitOfWork,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _autorizacao = autorizacao;
        _seguranca = seguranca;
        _repositorio = repositorio;
        _usuarios = usuarios;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    public async Task<IReadOnlyList<PerfilGestao>> ListarAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return await _repositorio.ListarPerfisAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResultadoGestao> CriarAsync(Guid tenantId, string? nome, string? descricao, IReadOnlyCollection<string> permissoes, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var nomeLimpo = nome?.Trim() ?? string.Empty;
        var problema = await ValidarNomeAsync(tenantId, nomeLimpo, null, cancellationToken).ConfigureAwait(false);
        if (problema is not null)
        {
            return problema;
        }

        var (ids, chaves, erroPermissoes) = await ResolverPermissoesAsync(permissoes, cancellationToken).ConfigureAwait(false);
        if (erroPermissoes is not null)
        {
            return erroPermissoes;
        }

        var escalada = await VerificarEscaladaAsync(tenantId, ator, chaves, cancellationToken).ConfigureAwait(false);
        if (escalada is not null)
        {
            return escalada;
        }

        Papel perfil;
        try
        {
            perfil = Papel.CriarPersonalizado(tenantId, nomeLimpo, descricao);
        }
        catch (EntradaInvalidaException)
        {
            return ResultadoGestao.Invalido($"Nome de até {Papel.LimiteNome} caracteres e descrição de até {Papel.LimiteDescricao}.");
        }

        try
        {
            await _repositorio.AdicionarPerfilAsync(perfil, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
            await _repositorio.DefinirPermissoesDoPerfilAsync(perfil.Id, ids, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ViolacaoUnicidadeException)
        {
            return ResultadoGestao.Conflito("Já existe um perfil com este nome neste contribuinte.");
        }

        await AuditarAsync(tenantId, AcaoAuditoria.RoleCreated, perfil.Id, "perfil personalizado cadastrado", null,
            new Dictionary<string, string?> { ["nome"] = perfil.Nome, ["permissoes"] = string.Join(",", chaves) }, cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok("Perfil cadastrado. Já pode ser escolhido no cadastro dos usuários.", perfil.Id);
    }

    public async Task<ResultadoGestao> AlterarAsync(Guid tenantId, Guid perfilId, string? nome, string? descricao, IReadOnlyCollection<string> permissoes, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var (perfil, recusa) = await CarregarPersonalizadoAsync(tenantId, perfilId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        var nomeLimpo = nome?.Trim() ?? string.Empty;
        var problema = await ValidarNomeAsync(tenantId, nomeLimpo, perfil!.Id, cancellationToken).ConfigureAwait(false);
        if (problema is not null)
        {
            return problema;
        }

        var (ids, chaves, erroPermissoes) = await ResolverPermissoesAsync(permissoes, cancellationToken).ConfigureAwait(false);
        if (erroPermissoes is not null)
        {
            return erroPermissoes;
        }

        // Só o que ENTRA no perfil é conferido contra as permissões de quem altera (tirar é sempre possível).
        var atuais = await _repositorio.ChavesDosPerfisAsync([perfil.Id], cancellationToken).ConfigureAwait(false);
        var escalada = await VerificarEscaladaAsync(tenantId, ator, chaves.Except(atuais, StringComparer.Ordinal).ToList(), cancellationToken).ConfigureAwait(false);
        if (escalada is not null)
        {
            return escalada;
        }

        var nomeAntes = perfil.Nome;
        try
        {
            perfil.Alterar(nomeLimpo, descricao);
        }
        catch (EntradaInvalidaException)
        {
            return ResultadoGestao.Invalido($"Nome de até {Papel.LimiteNome} caracteres e descrição de até {Papel.LimiteDescricao}.");
        }

        try
        {
            await _repositorio.DefinirPermissoesDoPerfilAsync(perfil.Id, ids, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ViolacaoUnicidadeException)
        {
            return ResultadoGestao.Conflito("Já existe um perfil com este nome neste contribuinte.");
        }

        await AuditarAsync(tenantId, AcaoAuditoria.RoleUpdated, perfil.Id, "perfil personalizado alterado",
            new Dictionary<string, string?> { ["nome"] = nomeAntes, ["permissoes"] = string.Join(",", atuais) },
            new Dictionary<string, string?> { ["nome"] = perfil.Nome, ["permissoes"] = string.Join(",", chaves) }, cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok("Perfil salvo. As permissões valem a partir da próxima ação de quem tem este perfil.", perfil.Id);
    }

    public async Task<ResultadoGestao> ExcluirAsync(Guid tenantId, Guid perfilId, CancellationToken cancellationToken = default)
    {
        await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var (perfil, recusa) = await CarregarPersonalizadoAsync(tenantId, perfilId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        var emUso = await _repositorio.ContarVinculosComPerfilAsync(perfil!.Id, cancellationToken).ConfigureAwait(false);
        if (emUso > 0)
        {
            return ResultadoGestao.Conflito($"Este perfil está em uso por {emUso} usuário(s). Troque o perfil deles antes de excluir.");
        }

        await _repositorio.RemoverPerfilAsync(perfil, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.RoleDeleted, perfil.Id, "perfil personalizado excluído",
            new Dictionary<string, string?> { ["nome"] = perfil.Nome }, null, cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok("Perfil excluído.", perfil.Id);
    }

    private async Task<Guid> ExigirAdministradorAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new TenantContextAusenteException();
        }

        await _autorizacao.ExigirAdministrativoAsync(tenantId, Permissoes.AdminUsuarios, cancellationToken).ConfigureAwait(false);
        return Guid.TryParse(_seguranca.PrincipalId, out var ator) && ator != Guid.Empty ? ator : throw new NaoAutenticadoException();
    }

    /// <summary>Só perfis personalizados DESTE contribuinte mudam; os do sistema e os de outro contribuinte, não.</summary>
    private async Task<(Papel? Perfil, ResultadoGestao? Recusa)> CarregarPersonalizadoAsync(Guid tenantId, Guid perfilId, CancellationToken cancellationToken)
    {
        var perfil = perfilId == Guid.Empty ? null : await _repositorio.ObterPerfilAsync(perfilId, cancellationToken).ConfigureAwait(false);
        if (perfil is null || (perfil.TenantId is Guid dono && dono != tenantId))
        {
            return (null, ResultadoGestao.NaoEncontrado("Perfil não encontrado neste contribuinte."));
        }

        return perfil.EhDoSistema
            ? (null, ResultadoGestao.NaoPermitido("Os perfis do sistema não podem ser alterados nem excluídos. Crie um perfil personalizado a partir dele."))
            : (perfil, null);
    }

    private async Task<ResultadoGestao?> ValidarNomeAsync(Guid tenantId, string nome, Guid? exceto, CancellationToken cancellationToken)
    {
        if (nome.Length < TamanhoMinimoNome || nome.Length > Papel.LimiteNome)
        {
            return ResultadoGestao.Invalido($"O nome do perfil deve ter de {TamanhoMinimoNome} a {Papel.LimiteNome} caracteres.");
        }

        return await _repositorio.NomePerfilEmUsoAsync(tenantId, nome, exceto, cancellationToken).ConfigureAwait(false)
            ? ResultadoGestao.Conflito("Já existe um perfil com este nome (do sistema ou deste contribuinte).")
            : null;
    }

    /// <summary>Só chaves de CONTRIBUINTE conhecidas do catálogo; pelo menos uma.</summary>
    private async Task<(IReadOnlyCollection<Guid> Ids, IReadOnlyList<string> Chaves, ResultadoGestao? Erro)> ResolverPermissoesAsync(
        IReadOnlyCollection<string>? permissoes, CancellationToken cancellationToken)
    {
        var chaves = (permissoes ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        if (chaves.Count == 0)
        {
            return ([], [], ResultadoGestao.Invalido("Escolha pelo menos uma permissão."));
        }

        var catalogo = await _repositorio.PermissoesPorChaveAsync(cancellationToken).ConfigureAwait(false);
        if (chaves.Any(c => Permissoes.EhPlataforma(c) || !catalogo.ContainsKey(c)))
        {
            return ([], [], ResultadoGestao.Invalido("Há permissões desconhecidas ou de plataforma na seleção. Atualize a página."));
        }

        return ([.. chaves.Select(c => catalogo[c])], chaves, null);
    }

    private async Task<ResultadoGestao?> VerificarEscaladaAsync(Guid tenantId, Guid ator, IReadOnlyCollection<string> chaves, CancellationToken cancellationToken)
    {
        if (chaves.Count == 0)
        {
            return null;
        }

        var doAtor = await _usuarios.ObterPermissoesAsync(ator, tenantId, cancellationToken).ConfigureAwait(false);
        var alem = chaves.Where(c => !doAtor.Possui(c)).ToList();
        if (alem.Count == 0)
        {
            return null;
        }

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(ator.ToString(), tenantId.ToString(), AcaoAuditoria.PrivilegeEscalationAttempt, ResultadoAuditoria.Negado, _relogio.AgoraUtc)
            {
                TipoRecurso = "Perfil",
                Motivo = "tentativa de pôr num perfil permissões que o administrador não tem",
                ValoresNovos = new Dictionary<string, string?> { ["permissoes"] = string.Join(",", alem) },
            },
            cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.NaoPermitido($"Você não pode pôr num perfil permissões que você mesmo não tem: {string.Join(", ", alem)}.");
    }

    private ValueTask AuditarAsync(
        Guid tenantId,
        string acao,
        Guid perfilId,
        string motivo,
        IReadOnlyDictionary<string, string?>? antes,
        IReadOnlyDictionary<string, string?>? novos,
        CancellationToken cancellationToken)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", tenantId.ToString(), acao, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = "Perfil",
                RecursoId = perfilId.ToString(),
                Motivo = motivo,
                ValoresAnteriores = antes,
                ValoresNovos = novos,
            },
            cancellationToken);
}
