using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Web.Seguranca;

namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// FACHADAS do CADASTRO de usuários e perfis. Embrulham os casos de uso GestaoUsuarios/GestaoPerfis; o tenant é o
// RESOLVIDO no servidor (SelecaoTenant.TenantAtual) — nunca do cliente. Autorização, anti-escalada, autoconcessão e
// auditoria são decididas no SERVIDOR; aqui só se traduz o resultado para a tela (mensagem segura, sem detalhe interno).
// =====================================================================================================

public interface IDadosGestaoUsuarios
{
    bool EnvioDeLinkDisponivel { get; }

    Task<ResultadoAcaoVm> CriarAsync(NovoUsuario dados, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> DarAcessoPorEmailAsync(string? email, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> AlterarDadosAsync(Guid usuarioId, string? nome, string? email, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> DefinirSituacaoAsync(Guid usuarioId, bool ativo, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> DesbloquearAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> DefinirPerfisAsync(Guid usuarioId, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> ExcluirAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> EnviarLinkSenhaAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Gera uma senha temporária e envia ao e-mail do usuário (troca obrigatória no próximo acesso).</summary>
    Task<ResultadoAcaoVm> GerarSenhaTemporariaAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Liga/desliga a exigência de MFA (o cadastro acontece no próximo acesso do usuário).</summary>
    Task<ResultadoAcaoVm> DefinirMfaObrigatorioAsync(Guid usuarioId, bool obrigatorio, CancellationToken cancellationToken = default);

    /// <summary>Remove o MFA do usuário (perdeu o celular) — exige o MFA de quem remove.</summary>
    Task<ResultadoAcaoVm> RemoverMfaAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> DefinirAdministradorPlataformaAsync(Guid usuarioId, bool administrador, CancellationToken cancellationToken = default);

    /// <summary>Contribuintes que quem pede administra (o da sessão primeiro) — para o "+ Novo usuário".</summary>
    Task<(IReadOnlyList<ContribuinteDoUsuario> Itens, EstadoConsultaUi Estado)> ListarContribuintesAdministradosAsync(CancellationToken cancellationToken = default);

    /// <summary>Seção "Contribuintes" do cadastro: os que quem pede administra, com a situação do usuário em cada um.</summary>
    Task<(IReadOnlyList<ContribuinteDoUsuario> Itens, EstadoConsultaUi Estado)> ListarContribuintesDoUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Acesso nos marcados (com os mesmos perfis do sistema), revogado nos desmarcados.</summary>
    Task<ResultadoAcaoVm> SalvarContribuintesAsync(Guid usuarioId, IReadOnlyCollection<Guid> marcados, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default);

    /// <summary>Depois do cadastro/"Dar acesso" no contribuinte da sessão: acesso também aos outros escolhidos.</summary>
    Task<ResultadoAcaoVm> DarAcessoEmOutrosAsync(Guid usuarioId, IReadOnlyCollection<Guid> outros, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default);

    /// <summary>Confere ANTES de cadastrar: para vários contribuintes, só perfis do sistema. Nulo = pode seguir.</summary>
    Task<ResultadoAcaoVm?> ValidarPerfisParaVariosAsync(IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default);
}

public sealed class DadosGestaoUsuarios : IDadosGestaoUsuarios
{
    private readonly GestaoUsuarios _gestao;
    private readonly GestaoContribuintesDoUsuario _contribuintes;
    private readonly SelecaoTenant _selecao;

    public DadosGestaoUsuarios(GestaoUsuarios gestao, GestaoContribuintesDoUsuario contribuintes, SelecaoTenant selecao)
    {
        ArgumentNullException.ThrowIfNull(gestao);
        ArgumentNullException.ThrowIfNull(contribuintes);
        ArgumentNullException.ThrowIfNull(selecao);
        _gestao = gestao;
        _contribuintes = contribuintes;
        _selecao = selecao;
    }

    public bool EnvioDeLinkDisponivel => _gestao.EnvioDeLinkDisponivel;

    public Task<ResultadoAcaoVm> CriarAsync(NovoUsuario dados, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.CriarAsync(tenant, dados, cancellationToken), "Usuário cadastrado");

    public Task<ResultadoAcaoVm> DarAcessoPorEmailAsync(string? email, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.DarAcessoPorEmailAsync(tenant, email, perfis, cancellationToken), "Acesso concedido");

    public Task<ResultadoAcaoVm> AlterarDadosAsync(Guid usuarioId, string? nome, string? email, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.AlterarDadosAsync(tenant, usuarioId, nome, email, cancellationToken), "Dados salvos");

    public Task<ResultadoAcaoVm> DefinirSituacaoAsync(Guid usuarioId, bool ativo, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.DefinirSituacaoAsync(tenant, usuarioId, ativo, cancellationToken), ativo ? "Usuário ativado" : "Usuário inativado");

    public Task<ResultadoAcaoVm> DesbloquearAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.DesbloquearAsync(tenant, usuarioId, cancellationToken), "Bloqueio liberado");

    public Task<ResultadoAcaoVm> DefinirPerfisAsync(Guid usuarioId, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.DefinirPerfisAsync(tenant, usuarioId, perfis, cancellationToken), "Perfis salvos");

    public Task<ResultadoAcaoVm> ExcluirAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.ExcluirAsync(tenant, usuarioId, cancellationToken), "Usuário excluído");

    public Task<ResultadoAcaoVm> EnviarLinkSenhaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.EnviarLinkSenhaAsync(tenant, usuarioId, cancellationToken), "Link enviado");

    public Task<ResultadoAcaoVm> GerarSenhaTemporariaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.GerarSenhaTemporariaAsync(tenant, usuarioId, cancellationToken), "Senha temporária enviada");

    public Task<ResultadoAcaoVm> DefinirMfaObrigatorioAsync(Guid usuarioId, bool obrigatorio, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.DefinirMfaObrigatorioAsync(tenant, usuarioId, obrigatorio, cancellationToken),
            obrigatorio ? "MFA exigido" : "MFA não exigido");

    public Task<ResultadoAcaoVm> RemoverMfaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.RemoverMfaAsync(tenant, usuarioId, cancellationToken), "MFA removido");

    public async Task<ResultadoAcaoVm> DefinirAdministradorPlataformaAsync(Guid usuarioId, bool administrador, CancellationToken cancellationToken = default)
    {
        try
        {
            var resultado = await _gestao.DefinirAdministradorPlataformaAsync(usuarioId, administrador, cancellationToken).ConfigureAwait(false);
            return MapeadorGestao.Mapear(resultado, administrador ? "Administrador da plataforma" : "Perfil removido");
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return MapeadorAcaoAdmin.MapearExcecao(excecao);
        }
    }

    public Task<(IReadOnlyList<ContribuinteDoUsuario> Itens, EstadoConsultaUi Estado)> ListarContribuintesAdministradosAsync(CancellationToken cancellationToken = default)
        => ConsultarAsync(async tenant => await _contribuintes.ListarAdministradosAsync(tenant, cancellationToken).ConfigureAwait(false));

    public Task<(IReadOnlyList<ContribuinteDoUsuario> Itens, EstadoConsultaUi Estado)> ListarContribuintesDoUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        => ConsultarAsync(tenant => _contribuintes.ListarDoUsuarioAsync(tenant, usuarioId, cancellationToken));

    public Task<ResultadoAcaoVm> SalvarContribuintesAsync(Guid usuarioId, IReadOnlyCollection<Guid> marcados, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _contribuintes.SalvarAsync(tenant, usuarioId, marcados, perfis, cancellationToken), "Contribuintes salvos");

    public Task<ResultadoAcaoVm> DarAcessoEmOutrosAsync(Guid usuarioId, IReadOnlyCollection<Guid> outros, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _contribuintes.DarAcessoEmOutrosAsync(tenant, usuarioId, outros, perfis, cancellationToken), "Acesso aos outros contribuintes");

    public async Task<ResultadoAcaoVm?> ValidarPerfisParaVariosAsync(IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default)
    {
        try
        {
            var problema = await _contribuintes.ValidarPerfisDoSistemaAsync(perfis, cancellationToken).ConfigureAwait(false);
            return problema is null ? null : MapeadorGestao.Mapear(problema, string.Empty);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return MapeadorAcaoAdmin.MapearExcecao(excecao);
        }
    }

    // Consulta no contribuinte da sessão; autorização negada vira estado da tela (nunca detalhe interno).
    private async Task<(IReadOnlyList<ContribuinteDoUsuario> Itens, EstadoConsultaUi Estado)> ConsultarAsync(
        Func<Guid, Task<IReadOnlyList<ContribuinteDoUsuario>?>> consulta)
    {
        if (_selecao.TenantAtual is not Guid tenant)
        {
            return ([], EstadoConsultaUi.SemTenant);
        }

        try
        {
            var itens = await consulta(tenant).ConfigureAwait(false);
            return itens is null ? ([], EstadoConsultaUi.SemPermissao) : (itens, EstadoConsultaUi.Ok);
        }
        catch (Exception excecao) when (excecao is AutorizacaoNegadaException or TenantNaoAutorizadoException or MfaRequeridoException or NaoAutenticadoException)
        {
            return ([], EstadoConsultaUi.SemPermissao);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return ([], EstadoConsultaUi.Erro);
        }
    }

    private async Task<ResultadoAcaoVm> NoContribuinteAsync(Func<Guid, Task<ResultadoGestao>> operacao, string tituloSucesso)
    {
        if (_selecao.TenantAtual is not Guid tenant)
        {
            return MapeadorAcaoAdmin.SemTenant();
        }

        try
        {
            return MapeadorGestao.Mapear(await operacao(tenant).ConfigureAwait(false), tituloSucesso);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return MapeadorAcaoAdmin.MapearExcecao(excecao);
        }
    }
}

// ---- Perfis ------------------------------------------------------------------------------------------

public interface IDadosPerfis
{
    Task<(IReadOnlyList<PerfilGestao> Perfis, EstadoConsultaUi Estado)> ListarAsync(CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> CriarAsync(string? nome, string? descricao, IReadOnlyCollection<string> permissoes, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> AlterarAsync(Guid perfilId, string? nome, string? descricao, IReadOnlyCollection<string> permissoes, CancellationToken cancellationToken = default);

    Task<ResultadoAcaoVm> ExcluirAsync(Guid perfilId, CancellationToken cancellationToken = default);
}

public sealed class DadosPerfis : IDadosPerfis
{
    private readonly GestaoPerfis _gestao;
    private readonly SelecaoTenant _selecao;

    public DadosPerfis(GestaoPerfis gestao, SelecaoTenant selecao)
    {
        ArgumentNullException.ThrowIfNull(gestao);
        ArgumentNullException.ThrowIfNull(selecao);
        _gestao = gestao;
        _selecao = selecao;
    }

    public async Task<(IReadOnlyList<PerfilGestao> Perfis, EstadoConsultaUi Estado)> ListarAsync(CancellationToken cancellationToken = default)
    {
        if (_selecao.TenantAtual is not Guid tenant)
        {
            return ([], EstadoConsultaUi.SemTenant);
        }

        try
        {
            return (await _gestao.ListarAsync(tenant, cancellationToken).ConfigureAwait(false), EstadoConsultaUi.Ok);
        }
        catch (Exception excecao) when (AdminUi.EhAutorizacao(excecao))
        {
            return ([], EstadoConsultaUi.SemPermissao);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return ([], EstadoConsultaUi.Erro);
        }
    }

    public Task<ResultadoAcaoVm> CriarAsync(string? nome, string? descricao, IReadOnlyCollection<string> permissoes, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.CriarAsync(tenant, nome, descricao, permissoes, cancellationToken), "Perfil cadastrado");

    public Task<ResultadoAcaoVm> AlterarAsync(Guid perfilId, string? nome, string? descricao, IReadOnlyCollection<string> permissoes, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.AlterarAsync(tenant, perfilId, nome, descricao, permissoes, cancellationToken), "Perfil salvo");

    public Task<ResultadoAcaoVm> ExcluirAsync(Guid perfilId, CancellationToken cancellationToken = default)
        => NoContribuinteAsync(tenant => _gestao.ExcluirAsync(tenant, perfilId, cancellationToken), "Perfil excluído");

    private async Task<ResultadoAcaoVm> NoContribuinteAsync(Func<Guid, Task<ResultadoGestao>> operacao, string tituloSucesso)
    {
        if (_selecao.TenantAtual is not Guid tenant)
        {
            return MapeadorAcaoAdmin.SemTenant();
        }

        try
        {
            return MapeadorGestao.Mapear(await operacao(tenant).ConfigureAwait(false), tituloSucesso);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return MapeadorAcaoAdmin.MapearExcecao(excecao);
        }
    }
}

/// <summary>Traduz o desfecho do cadastro para o resultado de tela (a mensagem já é segura, escrita no caso de uso).</summary>
public static class MapeadorGestao
{
    public static ResultadoAcaoVm Mapear(ResultadoGestao resultado, string tituloSucesso)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        return resultado.Desfecho switch
        {
            DesfechoGestao.Ok => new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, tituloSucesso, resultado.Mensagem, resultado.Id),
            DesfechoGestao.Conflito => new ResultadoAcaoVm(false, EstadoAcaoUi.Conflito, "Não foi possível salvar", resultado.Mensagem),
            DesfechoGestao.NaoPermitido => new ResultadoAcaoVm(false, EstadoAcaoUi.SemPermissao, "Operação não permitida", resultado.Mensagem),
            DesfechoGestao.NaoEncontrado => new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Não encontrado", resultado.Mensagem),
            _ => new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Confira os dados", resultado.Mensagem),
        };
    }
}
