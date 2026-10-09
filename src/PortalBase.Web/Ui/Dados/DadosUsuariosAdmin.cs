using PortalBase.Application.Administracao;
using PortalBase.Application.Identidade;
using PortalBase.Domain.Catalogo;
using PortalBase.Web.Seguranca;
using PortalBase.Web.Ui.Notificacoes;

namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// FACHADAS de ADMINISTRAÇÃO de USUÁRIOS e AÇÕES administrativas REAIS (Prompt 72). A LEITURA consome o
// read-model autorizado ConsultaUsuariosAutorizada; as AÇÕES embrulham COMANDOS REAIS já existentes
// (GestaoAcesso, CicloVidaTenant). O tenant é o RESOLVIDO no servidor
// (SelecaoTenant.TenantAtual) — nunca do cliente. Autorização, MFA, concorrência, idempotência e
// auditoria são decididos no SERVIDOR; a UI só renderiza o estado e nunca duplica regra (§18/§22).
// =====================================================================================================

// ---- Leitura: listagem e detalhe de usuários por tenant ---------------------------------------------

/// <summary>Filtro da UI para a listagem administrativa de usuários (mapeado ao filtro do read-model).</summary>
public sealed record FiltroUsuariosAdminVm : FiltroPaginadoVm
{
    public string? Busca { get; init; }

    public string Status { get; init; } = "Todos";

    public string StatusAcesso { get; init; } = "Todos";

    public string Ordenacao { get; init; } = nameof(CampoOrdenacaoUsuario.Login);

    public bool Descendente { get; init; }
}

/// <summary>Projeção de UI de um usuário do tenant (só campos seguros — nunca hash/segredo/stamp).</summary>
public sealed record UsuarioAdminItemVm(
    Guid UsuarioId,
    string Login,
    string Nome,
    string Email,
    string StatusRotulo,
    string StatusBadge,
    string AcessoRotulo,
    string AcessoBadge,
    bool MfaHabilitado,
    DateTimeOffset? BloqueioTemporarioAteUtc,
    string Papeis,
    DateTimeOffset? UltimoAcessoUtc,
    DateTimeOffset CriadoEmUtc,
    bool AdministradorPlataforma = false,
    bool TemOutrosContribuintes = false,
    IReadOnlyList<Guid>? PerfilIds = null,
    bool PossuiSenha = true,
    bool Bloqueado = false,
    bool MfaObrigatorio = false,
    bool SenhaTemporaria = false);

public interface IDadosUsuariosAdmin
{
    Task<ResultadoListaVm<UsuarioAdminItemVm>> ListarAsync(FiltroUsuariosAdminVm filtro, CancellationToken cancellationToken = default);

    Task<(UsuarioAdminItemVm? Item, EstadoConsultaUi Estado)> ObterAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}

public sealed class DadosUsuariosAdmin : IDadosUsuariosAdmin
{
    private readonly IConsultaUsuariosAutorizada _consulta;
    private readonly SelecaoTenant _selecao;

    public DadosUsuariosAdmin(IConsultaUsuariosAutorizada consulta, SelecaoTenant selecao)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(selecao);
        _consulta = consulta;
        _selecao = selecao;
    }

    public async Task<ResultadoListaVm<UsuarioAdminItemVm>> ListarAsync(FiltroUsuariosAdminVm filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);

        if (_selecao.TenantAtual is null)
        {
            return ResultadoListaVm.Vazio<UsuarioAdminItemVm>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.SemTenant);
        }

        var appFiltro = new FiltroUsuariosAdmin
        {
            Busca = filtro.Busca,
            Status = UsuariosAdminUi.ParseStatus(filtro.Status),
            StatusAcesso = UsuariosAdminUi.ParseAcesso(filtro.StatusAcesso),
            Ordenacao = UsuariosAdminUi.ParseOrdenacao(filtro.Ordenacao),
            Descendente = filtro.Descendente,
            Pagina = filtro.Pagina,
            TamanhoPagina = filtro.TamanhoPagina,
        };

        try
        {
            var r = await _consulta.ListarAsync(appFiltro, cancellationToken).ConfigureAwait(false);
            var itens = r.Itens.Select(UsuariosAdminUi.Mapear).ToList();
            return new ResultadoListaVm<UsuarioAdminItemVm>(itens, r.Pagina, r.TamanhoPagina, r.TotalItens, EstadoConsultaUi.Ok);
        }
        catch (Exception excecao) when (AdminUi.EhAutorizacao(excecao))
        {
            return ResultadoListaVm.Vazio<UsuarioAdminItemVm>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.SemPermissao);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return ResultadoListaVm.Vazio<UsuarioAdminItemVm>(filtro.Pagina, filtro.TamanhoPagina, EstadoConsultaUi.Erro);
        }
    }

    public async Task<(UsuarioAdminItemVm? Item, EstadoConsultaUi Estado)> ObterAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (_selecao.TenantAtual is null)
        {
            return (null, EstadoConsultaUi.SemTenant);
        }

        try
        {
            var dto = await _consulta.ObterAsync(usuarioId, cancellationToken).ConfigureAwait(false);
            return dto is null ? (null, EstadoConsultaUi.Ok) : (UsuariosAdminUi.Mapear(dto), EstadoConsultaUi.Ok);
        }
        catch (Exception excecao) when (AdminUi.EhAutorizacao(excecao))
        {
            return (null, EstadoConsultaUi.SemPermissao);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return (null, EstadoConsultaUi.Erro);
        }
    }
}

// ---- Ações reais de usuário (revogar acesso / atribuir papel) — comandos existentes ------------------

public interface IDadosAcoesUsuario
{
    /// <summary>Revoga o acesso do usuário ao contribuinte CORRENTE e invalida as sessões dele (GestaoAcesso).</summary>
    Task<ResultadoAcaoVm> RevogarAcessoAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Atribui um papel ao vínculo ATIVO do usuário no contribuinte corrente (GestaoAcesso).</summary>
    Task<ResultadoAcaoVm> AtribuirPapelAsync(Guid usuarioId, PapelConhecido papel, CancellationToken cancellationToken = default);
}

public sealed class DadosAcoesUsuario : IDadosAcoesUsuario
{
    private readonly GestaoAcesso _gestao;
    private readonly SelecaoTenant _selecao;

    public DadosAcoesUsuario(GestaoAcesso gestao, SelecaoTenant selecao)
    {
        ArgumentNullException.ThrowIfNull(gestao);
        ArgumentNullException.ThrowIfNull(selecao);
        _gestao = gestao;
        _selecao = selecao;
    }

    public async Task<ResultadoAcaoVm> RevogarAcessoAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (_selecao.TenantAtual is not Guid tenantId)
        {
            return MapeadorAcaoAdmin.SemTenant();
        }

        if (usuarioId == Guid.Empty)
        {
            return new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Usuário inválido", "Usuário não identificado.");
        }

        try
        {
            // Comando REAL: autoriza (admin.usuarios), revoga o vínculo do tenant e rotaciona a marca de
            // segurança (sessões perdem privilégio na próxima revalidação). Idempotente: se já revogado, nada muda.
            await _gestao.RevogarAcessoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
            return new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, "Acesso revogado",
                "O acesso do usuário a este contribuinte foi revogado e as sessões dele foram invalidadas.");
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return MapeadorAcaoAdmin.MapearExcecao(excecao);
        }
    }

    public async Task<ResultadoAcaoVm> AtribuirPapelAsync(Guid usuarioId, PapelConhecido papel, CancellationToken cancellationToken = default)
    {
        if (_selecao.TenantAtual is not Guid tenantId)
        {
            return MapeadorAcaoAdmin.SemTenant();
        }

        if (usuarioId == Guid.Empty)
        {
            return new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido, "Usuário inválido", "Usuário não identificado.");
        }

        try
        {
            await _gestao.AtribuirPapelAsync(tenantId, usuarioId, papel, cancellationToken).ConfigureAwait(false);
            return new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, "Papel atribuído",
                "O papel foi atribuído ao vínculo do usuário neste contribuinte.");
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            FalhasUi.Registrar(excecao);
            return MapeadorAcaoAdmin.MapearExcecao(excecao);
        }
    }
}

// ---- Apresentação (rótulos/badges/parse) — só apresentação --------------------------------------------

/// <summary>Mapeamento de apresentação da administração de usuários (Prompt 72) — cor + TEXTO.</summary>
public static class UsuariosAdminUi
{
    public static UsuarioAdminItemVm Mapear(UsuarioAdministracaoDto d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var (statusRotulo, statusBadge) = Status(d.Status);
        var (acessoRotulo, acessoBadge) = Acesso(d.StatusAcesso);
        return new UsuarioAdminItemVm(
            d.UsuarioId, d.Login, d.Nome, d.Email, statusRotulo, statusBadge, acessoRotulo, acessoBadge,
            d.MfaHabilitado, d.BloqueioTemporarioAteUtc, string.Join(", ", d.Papeis),
            d.UltimoAcessoUtc, d.CriadoEmUtc,
            d.AdministradorPlataforma, d.TemOutrosContribuintes, d.PerfilIds ?? [], d.PossuiSenha,
            d.Status == StatusUsuario.Bloqueado || (d.BloqueioTemporarioAteUtc is { } ate && ate > DateTimeOffset.UtcNow),
            d.MfaObrigatorio,
            d.SenhaTemporaria);
    }

    public static (string Rotulo, string Badge) Status(StatusUsuario status) => status switch
    {
        StatusUsuario.Ativo => ("Ativo", "ok"),
        StatusUsuario.Inativo => ("Inativo", "neutral"),
        StatusUsuario.Bloqueado => ("Bloqueado", "bad"),
        _ => (status.ToString(), "neutral"),
    };

    public static (string Rotulo, string Badge) Acesso(StatusAcesso status) => status switch
    {
        StatusAcesso.Ativo => ("Ativo", "ok"),
        StatusAcesso.Revogado => ("Revogado", "bad"),
        _ => (status.ToString(), "neutral"),
    };

    /// <summary>Opções de status (derivadas do enum de domínio — sem hardcode).</summary>
    public static IReadOnlyList<string> OpcoesStatus { get; } = Enum.GetNames<StatusUsuario>();

    public static IReadOnlyList<string> OpcoesAcesso { get; } = Enum.GetNames<StatusAcesso>();

    public static IReadOnlyList<string> OpcoesOrdenacao { get; } = Enum.GetNames<CampoOrdenacaoUsuario>();

    public static IReadOnlyList<string> Papeis { get; } = Enum.GetNames<PapelConhecido>();

    public static StatusUsuario? ParseStatus(string? valor) => ParseEnum<StatusUsuario>(valor);

    public static StatusAcesso? ParseAcesso(string? valor) => ParseEnum<StatusAcesso>(valor);

    public static CampoOrdenacaoUsuario ParseOrdenacao(string? valor) =>
        Enum.TryParse<CampoOrdenacaoUsuario>(valor, ignoreCase: true, out var v) ? v : CampoOrdenacaoUsuario.Login;

    private static T? ParseEnum<T>(string? valor) where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(valor)
            && !string.Equals(valor, "Todos", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<T>(valor, ignoreCase: true, out var v)
            ? v
            : null;
}

/// <summary>Mapeia exceções de comando administrativo para um resultado de UI seguro (nunca vaza detalhe).</summary>
public static class MapeadorAcaoAdmin
{
    public static ResultadoAcaoVm SemTenant() => new(false, EstadoAcaoUi.SemTenant, "Selecione um contribuinte",
        "A operação ocorre no contexto de um contribuinte autorizado.");

    /// <summary>Roteia o resultado de uma ação para o toast adequado (sucesso/aviso/erro) — só apresentação.</summary>
    public static void Notificar(ServicoNotificacoes notificacoes, ResultadoAcaoVm resultado)
    {
        ArgumentNullException.ThrowIfNull(notificacoes);
        ArgumentNullException.ThrowIfNull(resultado);
        if (resultado.Sucesso)
        {
            notificacoes.Sucesso(resultado.Titulo, resultado.Mensagem);
        }
        else if (resultado.Estado is EstadoAcaoUi.MfaRequerida or EstadoAcaoUi.Conflito or EstadoAcaoUi.SemTenant or EstadoAcaoUi.Invalido)
        {
            notificacoes.Aviso(resultado.Titulo, resultado.Mensagem);
        }
        else
        {
            notificacoes.Erro(resultado.Titulo, resultado.Mensagem);
        }
    }

    /// <summary>
    /// Prompt 73 (P2-16 / F70-04): caminho de STEP-UP de MFA a partir da URL atual. Ao receber
    /// <see cref="EstadoAcaoUi.MfaRequerida"/> a página navega para o 2º fator levando o <c>returnUrl</c>; o
    /// endpoint <c>/conta/mfa</c> valida (GestaoMfa), reemite o cookie com o fator e retorna à origem
    /// (<c>RedirecionamentoSeguro.Local</c> sanitiza). Só devolve caminho RELATIVO (nunca host externo).
    /// </summary>
    public static string CaminhoStepUpMfa(string uriAtual)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriAtual);
        var relativo = Uri.TryCreate(uriAtual, UriKind.Absolute, out var abs) ? abs.PathAndQuery : uriAtual;
        if (string.IsNullOrEmpty(relativo) || relativo[0] != '/')
        {
            relativo = "/";
        }

        return $"/conta/verificar?returnUrl={Uri.EscapeDataString(relativo)}";
    }

    public static ResultadoAcaoVm MapearExcecao(Exception excecao)
    {
        ArgumentNullException.ThrowIfNull(excecao);
        return excecao switch
        {
            PortalBase.Application.Excecoes.TenantContextAusenteException => SemTenant(),
            PortalBase.Application.Excecoes.MfaRequeridoException => new ResultadoAcaoVm(false, EstadoAcaoUi.MfaRequerida,
                "Verificação em duas etapas exigida", "Esta operação exige o segundo fator (MFA). Confirme e tente novamente."),
            PortalBase.Application.Excecoes.AutoConcessaoNaoPermitidaException => new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido,
                "Operação não permitida", "Um administrador não concede/altera o próprio privilégio (separação de deveres)."),
            PortalBase.Application.Excecoes.NaoAutenticadoException => new ResultadoAcaoVm(false, EstadoAcaoUi.SemPermissao,
                "Sessão inválida", "Autentique-se novamente para concluir a operação."),
            PortalBase.Application.Excecoes.AutorizacaoNegadaException => new ResultadoAcaoVm(false, EstadoAcaoUi.SemPermissao,
                "Sem permissão", "Seu perfil não pode executar esta operação, ou o vínculo não está ativo. A decisão é do servidor."),
            PortalBase.Domain.Excecoes.EstadoInvalidoException => new ResultadoAcaoVm(false, EstadoAcaoUi.Conflito,
                "Ação não aplicável ao estado atual", "O estado atual não permite esta operação. Atualize e revise."),
            PortalBase.Domain.Excecoes.EntradaInvalidaException => new ResultadoAcaoVm(false, EstadoAcaoUi.Invalido,
                "Não aplicável", "Registro inexistente no contribuinte atual ou entrada inválida. Atualize a lista."),
            _ => new ResultadoAcaoVm(false, EstadoAcaoUi.Erro, "Não foi possível concluir",
                "A operação falhou ou não foi autorizada. Nenhum detalhe interno é exibido."),
        };
    }
}
