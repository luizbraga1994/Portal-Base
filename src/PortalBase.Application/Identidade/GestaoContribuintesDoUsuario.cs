using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;

namespace PortalBase.Application.Identidade;

/// <summary>Situação do usuário num contribuinte, na seção "Contribuintes" do cadastro.</summary>
public enum SituacaoNoContribuinte
{
    SemAcesso,
    Ativo,
    Revogado,
}

/// <summary>
/// Um contribuinte que QUEM PEDE administra, com a situação do usuário nele. <paramref name="Atual"/>: o contribuinte da
/// sessão (os perfis dele são os da própria tela). Nada além do que o administrador já pode ver.
/// </summary>
public sealed record ContribuinteDoUsuario(
    Guid TenantId, string Nome, string RazaoSocial, string Cnpj, bool Operacional, bool Atual,
    SituacaoNoContribuinte Situacao, IReadOnlyList<PerfilDoVinculo> Perfis);

/// <summary>
/// Acesso de UMA conta a VÁRIOS contribuintes (pedido de 08/10/2026): a mesma pessoa (login, senha, MFA) entra em N
/// contribuintes; aqui o administrador marca em quais, com os MESMOS perfis do sistema em todos, sem cadastrar de novo.
/// Só aparecem os contribuintes que quem pede ADMINISTRA (Administrador da plataforma: todos; os demais: aqueles em que
/// têm <c>admin.usuarios</c>) — a lista é montada no servidor e cada contribuinte recebido do cliente é conferido contra
/// ela (regras 2, 3, 4). Cada mudança é feita pelo caso de uso de UM contribuinte (<see cref="GestaoUsuarios"/>), que
/// confere de novo a permissão naquele contribuinte, a autoconcessão e a anti-escalada, e audita ali. Não é atômico
/// entre contribuintes: o resultado diz o que mudou em cada um, e repetir o pedido completa o que faltou (idempotente).
/// </summary>
public sealed class GestaoContribuintesDoUsuario
{
    /// <summary>Teto de contribuintes por pedido (entrada do cliente é limitada).</summary>
    public const int MaximoContribuintesPorPedido = 500;

    private readonly GestaoUsuarios _gestao;
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IConsultaAcessosUsuario _acessos;
    private readonly IRepositorioGestaoUsuarios _repositorio;
    private readonly IRepositorioUsuario _usuarios;
    private readonly IContextoSeguranca _seguranca;

    public GestaoContribuintesDoUsuario(
        GestaoUsuarios gestao,
        IServicoAutorizacao autorizacao,
        IConsultaAcessosUsuario acessos,
        IRepositorioGestaoUsuarios repositorio,
        IRepositorioUsuario usuarios,
        IContextoSeguranca seguranca)
    {
        ArgumentNullException.ThrowIfNull(gestao);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(acessos);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(seguranca);
        _gestao = gestao;
        _autorizacao = autorizacao;
        _acessos = acessos;
        _repositorio = repositorio;
        _usuarios = usuarios;
        _seguranca = seguranca;
    }

    /// <summary>
    /// Os contribuintes que quem pede administra (o da sessão primeiro), para escolher em quais um usuário NOVO também
    /// entra. Fail-closed: exige <c>admin.usuarios</c> no contribuinte da sessão.
    /// </summary>
    public async Task<IReadOnlyList<ContribuinteDoUsuario>> ListarAdministradosAsync(Guid tenantAtual, CancellationToken cancellationToken = default)
    {
        await ExigirAdministradorAsync(tenantAtual, cancellationToken).ConfigureAwait(false);
        return [.. (await AdministradosAsync(cancellationToken).ConfigureAwait(false))
            .Select(t => Item(t, tenantAtual, null))
            .OrderByDescending(c => c.Atual)];
    }

    /// <summary>
    /// A seção "Contribuintes" do cadastro de um usuário: cada contribuinte que quem pede administra, com a situação e os
    /// perfis do usuário nele. Nulo se o usuário não é deste contribuinte (invisível daqui — IDOR).
    /// </summary>
    public async Task<IReadOnlyList<ContribuinteDoUsuario>?> ListarDoUsuarioAsync(Guid tenantAtual, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        await ExigirAdministradorAsync(tenantAtual, cancellationToken).ConfigureAwait(false);
        var vinculos = usuarioId == Guid.Empty
            ? []
            : await _repositorio.ListarVinculosDoUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        if (!vinculos.Any(v => v.TenantId == tenantAtual))
        {
            return null;
        }

        var porTenant = vinculos.ToDictionary(v => v.TenantId);
        return [.. (await AdministradosAsync(cancellationToken).ConfigureAwait(false))
            .Select(t => Item(t, tenantAtual, porTenant.GetValueOrDefault(t.TenantId)))
            .OrderByDescending(c => c.Atual)];
    }

    /// <summary>
    /// Aplica a escolha da seção "Contribuintes": nos <paramref name="marcados"/>, o usuário passa a ter acesso com os
    /// <paramref name="perfis"/> (do sistema; os mesmos em todos); nos desmarcados em que tinha acesso, o acesso é
    /// revogado. O contribuinte da sessão não muda por aqui (tem os próprios botões na tela).
    /// </summary>
    public async Task<ResultadoGestao> SalvarAsync(
        Guid tenantAtual, Guid usuarioId, IReadOnlyCollection<Guid> marcados, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marcados);
        ArgumentNullException.ThrowIfNull(perfis);
        GarantirNaoEhOAtor(usuarioId);
        var lista = await ListarDoUsuarioAsync(tenantAtual, usuarioId, cancellationToken).ConfigureAwait(false);
        if (lista is null)
        {
            return ResultadoGestao.NaoEncontrado("Usuário não encontrado neste contribuinte.");
        }

        var escolhidos = marcados.Where(id => id != tenantAtual).ToHashSet();
        if (Recusar(escolhidos, lista) is { } recusa)
        {
            return recusa;
        }

        var outros = lista.Where(c => !c.Atual).ToList();
        var perfisPedidos = perfis.Where(p => p != Guid.Empty).ToHashSet();
        var precisaPerfis = outros.Any(c => escolhidos.Contains(c.TenantId));
        if (precisaPerfis && await ValidarPerfisDoSistemaAsync(perfisPedidos, cancellationToken).ConfigureAwait(false) is { } problema)
        {
            return problema;
        }

        var resultados = new List<(ContribuinteDoUsuario Contribuinte, ResultadoGestao Resultado, string Acao)>();
        foreach (var contribuinte in outros)
        {
            var quer = escolhidos.Contains(contribuinte.TenantId);
            var tem = contribuinte.Situacao == SituacaoNoContribuinte.Ativo;
            if (quer && !tem)
            {
                resultados.Add((contribuinte, await TentarAsync(() => _gestao.DarAcessoAsync(contribuinte.TenantId, usuarioId, perfisPedidos, cancellationToken))
                    .ConfigureAwait(false), "acesso concedido"));
            }
            else if (quer && !contribuinte.Perfis.Select(p => p.Id).ToHashSet().SetEquals(perfisPedidos))
            {
                resultados.Add((contribuinte, await TentarAsync(() => _gestao.DefinirPerfisAsync(contribuinte.TenantId, usuarioId, perfisPedidos, cancellationToken))
                    .ConfigureAwait(false), "perfis ajustados"));
            }
            else if (!quer && tem)
            {
                resultados.Add((contribuinte, await TentarAsync(() => _gestao.RevogarAcessoAsync(contribuinte.TenantId, usuarioId, cancellationToken))
                    .ConfigureAwait(false), "acesso revogado"));
            }
        }

        return Resumir(resultados, usuarioId, "Nada a mudar: os contribuintes marcados já estão com esses perfis.");
    }

    /// <summary>
    /// Depois de cadastrar (ou dar acesso a) um usuário no contribuinte da sessão, dá a ele acesso também aos
    /// <paramref name="outros"/> contribuintes escolhidos, com os mesmos perfis (do sistema).
    /// </summary>
    public async Task<ResultadoGestao> DarAcessoEmOutrosAsync(
        Guid tenantAtual, Guid usuarioId, IReadOnlyCollection<Guid> outros, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outros);
        ArgumentNullException.ThrowIfNull(perfis);
        var escolhidos = outros.Where(id => id != tenantAtual).ToHashSet();
        if (escolhidos.Count == 0)
        {
            return ResultadoGestao.Ok("Nenhum outro contribuinte escolhido.", usuarioId);
        }

        GarantirNaoEhOAtor(usuarioId);
        var lista = await ListarDoUsuarioAsync(tenantAtual, usuarioId, cancellationToken).ConfigureAwait(false);
        if (lista is null)
        {
            return ResultadoGestao.NaoEncontrado("Usuário não encontrado neste contribuinte.");
        }

        var perfisPedidos = perfis.Where(p => p != Guid.Empty).ToHashSet();
        if ((Recusar(escolhidos, lista) ?? await ValidarPerfisDoSistemaAsync(perfisPedidos, cancellationToken).ConfigureAwait(false)) is { } recusa)
        {
            return recusa;
        }

        var resultados = new List<(ContribuinteDoUsuario Contribuinte, ResultadoGestao Resultado, string Acao)>();
        foreach (var contribuinte in lista.Where(c => escolhidos.Contains(c.TenantId)))
        {
            var ativo = contribuinte.Situacao == SituacaoNoContribuinte.Ativo;
            var resultado = ativo
                ? await TentarAsync(() => _gestao.DefinirPerfisAsync(contribuinte.TenantId, usuarioId, perfisPedidos, cancellationToken)).ConfigureAwait(false)
                : await TentarAsync(() => _gestao.DarAcessoAsync(contribuinte.TenantId, usuarioId, perfisPedidos, cancellationToken)).ConfigureAwait(false);
            resultados.Add((contribuinte, resultado, ativo ? "perfis ajustados" : "acesso concedido"));
        }

        return Resumir(resultados, usuarioId, "Nenhum outro contribuinte escolhido.");
    }

    /// <summary>Os perfis valem em vários contribuintes: só os do SISTEMA (os personalizados são de um contribuinte só).</summary>
    public async Task<ResultadoGestao?> ValidarPerfisDoSistemaAsync(IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(perfis);
        if (perfis.Count == 0)
        {
            return ResultadoGestao.Invalido("Escolha pelo menos um perfil.");
        }

        foreach (var id in perfis)
        {
            var perfil = await _repositorio.ObterPerfilAsync(id, cancellationToken).ConfigureAwait(false);
            if (perfil is null || perfil.TenantId is not null)
            {
                return ResultadoGestao.Invalido(
                    "Para vários contribuintes, use só os perfis do sistema (Leitor, Analista, Administrador): um perfil personalizado vale em um contribuinte só.");
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<AcessoContribuinte>> AdministradosAsync(CancellationToken cancellationToken)
    {
        var ator = AtorAtual();
        var acessiveis = await _acessos.ListarDoUsuarioAsync(ator, cancellationToken).ConfigureAwait(false);

        // Administrador da plataforma administra todos (a autorização de cada contribuinte também o aceita): uma
        // consulta só, em vez de uma avaliação por contribuinte. Os demais têm poucos vínculos: confere um a um.
        if (await _usuarios.EhAdministradorPlataformaAsync(ator, cancellationToken).ConfigureAwait(false))
        {
            return acessiveis;
        }

        var administrados = new List<AcessoContribuinte>(acessiveis.Count);
        foreach (var contribuinte in acessiveis)
        {
            if ((await _autorizacao.AvaliarAdministrativoAsync(contribuinte.TenantId, Permissoes.AdminUsuarios, cancellationToken).ConfigureAwait(false)).Permitido)
            {
                administrados.Add(contribuinte);
            }
        }

        return administrados;
    }

    private static ContribuinteDoUsuario Item(AcessoContribuinte t, Guid tenantAtual, VinculoDoUsuario? vinculo)
        => new(t.TenantId, t.Nome, t.RazaoSocial, t.Cnpj, t.Operacional, t.TenantId == tenantAtual,
            vinculo is null ? SituacaoNoContribuinte.SemAcesso : vinculo.Ativo ? SituacaoNoContribuinte.Ativo : SituacaoNoContribuinte.Revogado,
            vinculo?.Perfis ?? []);

    /// <summary>Contribuinte que não está na lista montada no servidor (forjado ou sem permissão): recusa o pedido todo.</summary>
    private static ResultadoGestao? Recusar(HashSet<Guid> escolhidos, IReadOnlyList<ContribuinteDoUsuario> lista)
    {
        if (escolhidos.Count > MaximoContribuintesPorPedido)
        {
            return ResultadoGestao.Invalido($"Escolha no máximo {MaximoContribuintesPorPedido} contribuintes por vez.");
        }

        var permitidos = lista.Select(c => c.TenantId).ToHashSet();
        return escolhidos.All(permitidos.Contains)
            ? null
            : ResultadoGestao.NaoPermitido("Um dos contribuintes escolhidos não está disponível para você. Atualize a página.");
    }

    /// <summary>Permissão perdida num contribuinte no meio do pedido vira o resultado DAQUELE contribuinte; o resto segue.</summary>
    private static async Task<ResultadoGestao> TentarAsync(Func<Task<ResultadoGestao>> operacao)
    {
        try
        {
            return await operacao().ConfigureAwait(false);
        }
        catch (Exception excecao) when (excecao is AutorizacaoNegadaException or TenantNaoAutorizadoException or TenantContextAusenteException)
        {
            return ResultadoGestao.NaoPermitido("Você não tem mais permissão para administrar usuários neste contribuinte.");
        }
    }

    private static ResultadoGestao Resumir(
        List<(ContribuinteDoUsuario Contribuinte, ResultadoGestao Resultado, string Acao)> resultados, Guid usuarioId, string semMudanca)
    {
        if (resultados.Count == 0)
        {
            return ResultadoGestao.Ok(semMudanca, usuarioId);
        }

        var feitos = resultados.Where(r => r.Resultado.Sucesso).Select(r => $"{r.Contribuinte.Nome} ({r.Acao})").ToList();
        var falhas = resultados.Where(r => !r.Resultado.Sucesso).ToList();
        var texto = feitos.Count > 0 ? $"Feito em {feitos.Count} contribuinte(s): {string.Join(", ", feitos)}." : string.Empty;
        if (falhas.Count == 0)
        {
            return ResultadoGestao.Ok(texto, usuarioId);
        }

        var naoFeitos = string.Join(" ", falhas.Select(f => $"{f.Contribuinte.Nome}: {f.Resultado.Mensagem}"));
        return new ResultadoGestao(falhas[0].Resultado.Desfecho, $"{texto} Não foi possível em {falhas.Count}: {naoFeitos}".Trim(), usuarioId);
    }

    private async ValueTask ExigirAdministradorAsync(Guid tenantAtual, CancellationToken cancellationToken)
    {
        if (tenantAtual == Guid.Empty)
        {
            throw new TenantContextAusenteException();
        }

        await _autorizacao.ExigirAdministrativoAsync(tenantAtual, Permissoes.AdminUsuarios, cancellationToken).ConfigureAwait(false);
    }

    private Guid AtorAtual()
        => Guid.TryParse(_seguranca.PrincipalId, out var ator) && ator != Guid.Empty ? ator : throw new NaoAutenticadoException();

    private void GarantirNaoEhOAtor(Guid usuarioId)
    {
        if (AtorAtual() == usuarioId)
        {
            throw new AutoConcessaoNaoPermitidaException();
        }
    }
}
