using System.Text.RegularExpressions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Identidade;

/// <summary>Desfecho de uma operação de cadastro (usuários e perfis). Autorização negada continua sendo EXCEÇÃO.</summary>
public enum DesfechoGestao
{
    Ok,

    /// <summary>Entrada inválida (campo obrigatório, formato, política de senha…).</summary>
    Invalido,

    /// <summary>Conflito com o estado atual (e-mail já usado, perfil em uso…).</summary>
    Conflito,

    NaoEncontrado,

    /// <summary>Regra de segurança (autoconcessão, escalada de privilégio, usuário de outro contribuinte…).</summary>
    NaoPermitido,
}

/// <summary>Resultado de uma operação de cadastro, com a mensagem a mostrar ao usuário (nunca detalhe interno).</summary>
public sealed record ResultadoGestao(DesfechoGestao Desfecho, string Mensagem, Guid? Id = null)
{
    public bool Sucesso => Desfecho == DesfechoGestao.Ok;

    public static ResultadoGestao Ok(string mensagem, Guid? id = null) => new(DesfechoGestao.Ok, mensagem, id);

    public static ResultadoGestao Invalido(string mensagem) => new(DesfechoGestao.Invalido, mensagem);

    public static ResultadoGestao Conflito(string mensagem) => new(DesfechoGestao.Conflito, mensagem);

    public static ResultadoGestao NaoEncontrado(string mensagem) => new(DesfechoGestao.NaoEncontrado, mensagem);

    public static ResultadoGestao NaoPermitido(string mensagem) => new(DesfechoGestao.NaoPermitido, mensagem);
}

/// <summary>Perfil (papel) como o cadastro o enxerga: do sistema ou personalizado do contribuinte.</summary>
public sealed record PerfilGestao(Guid Id, string Nome, string Descricao, bool DoSistema, IReadOnlyList<string> Permissoes, int Usuarios);

/// <summary>
/// Dados do novo usuário (vindos do formulário: hostis até validados). Senha: <paramref name="GerarSenhaTemporaria"/>
/// (gerada e enviada por e-mail, troca obrigatória no 1º acesso) tem precedência; senão <paramref name="EnviarLinkSenha"/>
/// (link para criar a senha) ou <paramref name="SenhaInicial"/>. <paramref name="ExigirMfa"/>: o usuário cadastra o
/// autenticador no 1º acesso.
/// </summary>
public sealed record NovoUsuario(
    string? Login, string? Nome, string? Email, string? SenhaInicial, IReadOnlyCollection<Guid> Perfis, bool EnviarLinkSenha,
    bool ExigirMfa = false, bool GerarSenhaTemporaria = false);

/// <summary>Perfil de um vínculo, como a seção "Contribuintes" do cadastro o mostra.</summary>
public sealed record PerfilDoVinculo(Guid Id, string Nome, bool DoSistema);

/// <summary>Vínculo do usuário com um contribuinte (ativo ou revogado) e os perfis dele ali.</summary>
public sealed record VinculoDoUsuario(Guid TenantId, bool Ativo, IReadOnlyList<PerfilDoVinculo> Perfis);

/// <summary>Persistência do cadastro de usuários e perfis (catálogo). Entidades devolvidas são rastreadas para alteração.</summary>
public interface IRepositorioGestaoUsuarios
{
    Task<Usuario?> ObterUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    Task<bool> EmailEmUsoAsync(string emailNormalizado, Guid? exceto, CancellationToken cancellationToken = default);

    Task<bool> LoginEmUsoAsync(string login, Guid? exceto, CancellationToken cancellationToken = default);

    Task AdicionarUsuarioAsync(Usuario usuario, CancellationToken cancellationToken = default);

    Task<UsuarioTenant?> ObterVinculoAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default);

    Task AdicionarVinculoAsync(UsuarioTenant vinculo, CancellationToken cancellationToken = default);

    Task RemoverVinculoAsync(UsuarioTenant vinculo, CancellationToken cancellationToken = default);

    /// <summary>O usuário tem vínculo (ativo ou revogado) com algum OUTRO contribuinte?</summary>
    Task<bool> TemVinculoComOutroContribuinteAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Todos os vínculos (ativos e revogados) do usuário, com os perfis de cada um — duas consultas, sem N+1.</summary>
    Task<IReadOnlyList<VinculoDoUsuario>> ListarVinculosDoUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> PerfisDoVinculoAsync(Guid usuarioTenantId, CancellationToken cancellationToken = default);

    /// <summary>Substitui o conjunto de perfis do vínculo (remove os que saíram, inclui os novos).</summary>
    Task DefinirPerfisDoVinculoAsync(Guid usuarioTenantId, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default);

    /// <summary>Perfis do sistema + personalizados DESTE contribuinte, com permissões e quantos usuários (vínculos ativos) os têm.</summary>
    Task<IReadOnlyList<PerfilGestao>> ListarPerfisAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<Papel?> ObterPerfilAsync(Guid perfilId, CancellationToken cancellationToken = default);

    /// <summary>O nome já é usado por um perfil do sistema ou por outro perfil deste contribuinte (sem diferenciar maiúsculas)?</summary>
    Task<bool> NomePerfilEmUsoAsync(Guid tenantId, string nome, Guid? exceto, CancellationToken cancellationToken = default);

    Task AdicionarPerfilAsync(Papel perfil, CancellationToken cancellationToken = default);

    Task RemoverPerfilAsync(Papel perfil, CancellationToken cancellationToken = default);

    /// <summary>Permissões de CONTRIBUINTE cadastradas no catálogo (chave → id). As de plataforma não entram em perfis.</summary>
    Task<IReadOnlyDictionary<string, Guid>> PermissoesPorChaveAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ChavesDosPerfisAsync(IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken = default);

    Task DefinirPermissoesDoPerfilAsync(Guid perfilId, IReadOnlyCollection<Guid> permissoes, CancellationToken cancellationToken = default);

    Task<int> ContarVinculosComPerfilAsync(Guid perfilId, CancellationToken cancellationToken = default);
}

/// <summary>
/// CADASTRO de usuários do contribuinte (cadastrar, alterar, excluir, perfis, link de senha) e do perfil
/// "Administrador da plataforma". Tudo decidido no SERVIDOR: exige <c>admin.usuarios</c> no contribuinte (o tenant vem
/// da sessão, nunca do formulário); ninguém altera o PRÓPRIO privilégio; ninguém concede permissão que não tem
/// (anti-escalada); os dados de quem também é de OUTRO contribuinte (ou administrador da plataforma) só mudam pelas mãos
/// de um administrador da plataforma (evita tomada de conta entre contribuintes). Toda alteração é auditada.
/// </summary>
public sealed partial class GestaoUsuarios
{
    private const int MaximoPerfisPorUsuario = 20;
    private const int TamanhoMinimoLogin = 3;
    private const int TamanhoMaximoLogin = 128;

    private readonly IServicoAutorizacao _autorizacao;
    private readonly IAutorizacaoPlataforma _plataforma;
    private readonly IContextoSeguranca _seguranca;
    private readonly IRepositorioGestaoUsuarios _repositorio;
    private readonly IRepositorioUsuario _usuarios;
    private readonly IHasherSenha _hasher;
    private readonly RecuperacaoSenha _recuperacao;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;
    private readonly IGeradorTokens? _geradorTokens;

    public GestaoUsuarios(
        IServicoAutorizacao autorizacao,
        IAutorizacaoPlataforma plataforma,
        IContextoSeguranca seguranca,
        IRepositorioGestaoUsuarios repositorio,
        IRepositorioUsuario usuarios,
        IHasherSenha hasher,
        RecuperacaoSenha recuperacao,
        IUnitOfWork unitOfWork,
        IAuditoria auditoria,
        IRelogio relogio,
        IGeradorTokens? geradorTokens = null)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(plataforma);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(recuperacao);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _autorizacao = autorizacao;
        _plataforma = plataforma;
        _seguranca = seguranca;
        _repositorio = repositorio;
        _usuarios = usuarios;
        _hasher = hasher;
        _recuperacao = recuperacao;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _relogio = relogio;
        _geradorTokens = geradorTokens;
    }

    /// <summary>O envio de e-mail está ligado (o formulário oferece "enviar link para criar a senha").</summary>
    public bool EnvioDeLinkDisponivel => _recuperacao.Disponivel;

    // ------------------------------------------------------------------------------------------------- usuários

    public async Task<ResultadoGestao> CriarAsync(Guid tenantId, NovoUsuario dados, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dados);
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var login = dados.Login?.Trim() ?? string.Empty;
        var nome = dados.Nome?.Trim() ?? string.Empty;
        var email = Usuario.NormalizarEmail(dados.Email ?? string.Empty);
        if (nome.Length == 0)
        {
            return ResultadoGestao.Invalido("Informe o nome.");
        }

        if (!LoginValido(login))
        {
            return ResultadoGestao.Invalido("Usuário inválido: use de 3 a 128 caracteres (letras, números, ponto, hífen, sublinhado ou @), sem espaços.");
        }

        Usuario usuario;
        try
        {
            usuario = Usuario.Criar(login, nome, email, _relogio.AgoraUtc);
        }
        catch (EntradaInvalidaException)
        {
            return ResultadoGestao.Invalido("Informe um e-mail válido (ele é o login do usuário) e um nome de até 200 caracteres.");
        }

        if (await _repositorio.EmailEmUsoAsync(usuario.Email, null, cancellationToken).ConfigureAwait(false))
        {
            return ResultadoGestao.Conflito("Já existe um usuário com este e-mail. Para dar acesso a ele, use o quadro \"Dar acesso a um usuário que já existe\" (abaixo do cadastro).");
        }

        if (await _repositorio.LoginEmUsoAsync(login, null, cancellationToken).ConfigureAwait(false))
        {
            return ResultadoGestao.Conflito("Este nome de usuário já está em uso. Escolha outro.");
        }

        var perfis = Distintos(dados.Perfis);
        var problemaPerfis = await ValidarPerfisAsync(tenantId, perfis, cancellationToken).ConfigureAwait(false)
            ?? await VerificarEscaladaAsync(tenantId, ator, perfis, cancellationToken).ConfigureAwait(false);
        if (problemaPerfis is not null)
        {
            return problemaPerfis;
        }

        var temporaria = dados.GerarSenhaTemporaria;
        var senha = temporaria ? null : dados.SenhaInicial;
        if (temporaria)
        {
            if (!_recuperacao.Disponivel)
            {
                return ResultadoGestao.Invalido("O envio de e-mail não está configurado neste portal: informe uma senha inicial.");
            }

            senha = GerarSenhaTemporariaValida(login, usuario.Email);
        }
        else if (!string.IsNullOrEmpty(senha))
        {
            if (PoliticaSenha.Validar(senha, login, usuario.Email) is { } regra)
            {
                return ResultadoGestao.Invalido($"A senha inicial não atende às regras: {regra}");
            }
        }
        else if (!dados.EnviarLinkSenha)
        {
            return ResultadoGestao.Invalido("Informe a senha inicial ou marque o envio do link para o usuário criar a senha.");
        }
        else if (!_recuperacao.Disponivel)
        {
            return ResultadoGestao.Invalido("O envio de e-mail não está configurado neste portal: informe uma senha inicial.");
        }

        var agora = _relogio.AgoraUtc;
        if (temporaria)
        {
            usuario.DefinirSenhaTemporaria(_hasher.Gerar(senha!), agora);
        }
        else if (!string.IsNullOrEmpty(senha))
        {
            usuario.DefinirCredencial(_hasher.Gerar(senha), agora);
        }

        usuario.DefinirMfaObrigatorio(dados.ExigirMfa, agora);

        var vinculo = UsuarioTenant.Conceder(usuario.Id, tenantId, agora);
        try
        {
            await _unitOfWork.ExecutarEmTransacaoAsync(
                async ct =>
                {
                    await _repositorio.AdicionarUsuarioAsync(usuario, ct).ConfigureAwait(false);
                    await _repositorio.AdicionarVinculoAsync(vinculo, ct).ConfigureAwait(false);
                    await _unitOfWork.SalvarAlteracoesAsync(ct).ConfigureAwait(false);
                    await _repositorio.DefinirPerfisDoVinculoAsync(vinculo.Id, perfis, ct).ConfigureAwait(false);
                    await _unitOfWork.SalvarAlteracoesAsync(ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ViolacaoUnicidadeException)
        {
            return ResultadoGestao.Conflito("Já existe um usuário com este e-mail ou nome de usuário.");
        }

        await AuditarAsync(tenantId, AcaoAuditoria.UserCreated, usuario.Id, "usuário cadastrado e vinculado ao contribuinte",
            novos: new Dictionary<string, string?>
            {
                ["perfis"] = string.Join(",", perfis),
                // Chaves sem "senha"/"mfa": o sanitizador redigiria esses VALORES (que aqui não são segredo — regra 8).
                ["primeiroAcesso"] = temporaria ? "senha temporária por e-mail" : (string.IsNullOrEmpty(senha) ? "link por e-mail" : "senha definida pelo administrador"),
                ["segundoFatorExigido"] = dados.ExigirMfa ? "sim" : "não",
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var avisoMfa = dados.ExigirMfa ? " No primeiro acesso ele cadastra a verificação em duas etapas (MFA)." : string.Empty;
        if (temporaria)
        {
            var enviada = await _recuperacao.EnviarSenhaTemporariaAsync(usuario, senha!, cancellationToken).ConfigureAwait(false);
            return enviada
                ? ResultadoGestao.Ok($"Usuário cadastrado. Enviamos para {usuario.Email} uma senha temporária; no primeiro acesso ele cria a senha definitiva.{avisoMfa}", usuario.Id)
                : ResultadoGestao.Ok("Usuário cadastrado, mas o e-mail com a senha temporária não pôde ser enviado agora. Use \"Gerar senha temporária\" no cadastro dele.", usuario.Id);
        }

        if (string.IsNullOrEmpty(senha))
        {
            var envio = await _recuperacao.EnviarLinkPeloAdministradorAsync(usuario, novoUsuario: true, cancellationToken).ConfigureAwait(false);
            return envio == ResultadoEnvioLinkSenha.Enviado
                ? ResultadoGestao.Ok($"Usuário cadastrado. Enviamos para {usuario.Email} um link para criar a senha (vale 24 horas).{avisoMfa}", usuario.Id)
                : ResultadoGestao.Ok("Usuário cadastrado, mas o e-mail com o link não pôde ser enviado agora. Use \"Enviar link para criar a senha\" no cadastro dele.", usuario.Id);
        }

        return ResultadoGestao.Ok($"Usuário cadastrado. Ele já pode entrar com o e-mail e a senha inicial.{avisoMfa}", usuario.Id);
    }

    /// <summary>Dá acesso a este contribuinte a um usuário que JÁ existe (de outro contribuinte), pelo e-mail.</summary>
    public async Task<ResultadoGestao> DarAcessoPorEmailAsync(Guid tenantId, string? email, IReadOnlyCollection<Guid> perfisInformados, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var normalizado = Usuario.NormalizarEmail(email ?? string.Empty);
        var usuario = normalizado.Length is 0 or > Usuario.TamanhoMaximoEmail
            ? null
            : await _usuarios.ObterPorEmailAsync(normalizado, cancellationToken).ConfigureAwait(false);
        if (usuario is null)
        {
            return ResultadoGestao.NaoEncontrado("Nenhum usuário com este e-mail. Para criar um, use \"Novo usuário\".");
        }

        return await ConcederAcessoAsync(tenantId, ator, usuario, perfisInformados, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dá (ou devolve) a ESTE contribuinte o acesso de um usuário identificado pelo id — usado pela gestão de vários
    /// contribuintes, que só passa usuários visíveis a quem pede. Mesmas regras do "Dar acesso" por e-mail.
    /// </summary>
    internal async Task<ResultadoGestao> DarAcessoAsync(Guid tenantId, Guid usuarioId, IReadOnlyCollection<Guid> perfisInformados, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var usuario = usuarioId == Guid.Empty ? null : await _repositorio.ObterUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        return usuario is null
            ? ResultadoGestao.NaoEncontrado("Usuário não encontrado.")
            : await ConcederAcessoAsync(tenantId, ator, usuario, perfisInformados, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResultadoGestao> ConcederAcessoAsync(
        Guid tenantId, Guid ator, Usuario usuario, IReadOnlyCollection<Guid> perfisInformados, CancellationToken cancellationToken)
    {
        if (usuario.Id == ator)
        {
            throw new AutoConcessaoNaoPermitidaException();
        }

        var perfis = Distintos(perfisInformados);
        var problema = await ValidarPerfisAsync(tenantId, perfis, cancellationToken).ConfigureAwait(false)
            ?? await VerificarEscaladaAsync(tenantId, ator, perfis, cancellationToken).ConfigureAwait(false);
        if (problema is not null)
        {
            return problema;
        }

        var agora = _relogio.AgoraUtc;
        var vinculo = await _repositorio.ObterVinculoAsync(usuario.Id, tenantId, cancellationToken).ConfigureAwait(false);
        if (vinculo is { EstaAtivo: true })
        {
            return ResultadoGestao.Conflito("Este usuário já tem acesso a este contribuinte. Altere os perfis no cadastro dele.");
        }

        if (vinculo is null)
        {
            vinculo = UsuarioTenant.Conceder(usuario.Id, tenantId, agora);
            await _repositorio.AdicionarVinculoAsync(vinculo, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            vinculo.Reativar();
        }

        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await _repositorio.DefinirPerfisDoVinculoAsync(vinculo.Id, perfis, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.TenantUserLinked, usuario.Id, "acesso concedido a usuário existente",
            novos: new Dictionary<string, string?> { ["perfis"] = string.Join(",", perfis) }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok("Acesso concedido. O usuário já pode escolher este contribuinte ao entrar.", usuario.Id);
    }

    public async Task<ResultadoGestao> AlterarDadosAsync(Guid tenantId, Guid usuarioId, string? nome, string? email, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var (usuario, _, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        if (usuario!.Id != ator && !await PodeGerirIdentidadeAsync(ator, usuario, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return NaoPermitidoOutroContribuinte();
        }

        var emailNormalizado = Usuario.NormalizarEmail(email ?? string.Empty);
        if (await _repositorio.EmailEmUsoAsync(emailNormalizado, usuario.Id, cancellationToken).ConfigureAwait(false))
        {
            return ResultadoGestao.Conflito("Já existe outro usuário com este e-mail.");
        }

        var antes = new Dictionary<string, string?> { ["nome"] = usuario.Nome, ["emailAlterado"] = "não" };
        var emailMudou = !string.Equals(usuario.Email, emailNormalizado, StringComparison.Ordinal);
        try
        {
            usuario.AlterarDados(nome ?? string.Empty, email ?? string.Empty, _relogio.AgoraUtc);
        }
        catch (EntradaInvalidaException)
        {
            return ResultadoGestao.Invalido("Informe um nome (até 200 caracteres) e um e-mail válido.");
        }

        try
        {
            await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ViolacaoUnicidadeException)
        {
            return ResultadoGestao.Conflito("Já existe outro usuário com este e-mail.");
        }

        if (emailMudou)
        {
            // Um link de senha enviado ao e-mail ANTIGO não pode mais servir.
            await _usuarios.InvalidarTokensRecuperacaoAsync(usuario.Id, _relogio.AgoraUtc, cancellationToken).ConfigureAwait(false);
        }

        // E-mail é dado pessoal: a trilha registra QUE mudou, não o valor.
        await AuditarAsync(tenantId, AcaoAuditoria.UserUpdated, usuario.Id, "dados do usuário alterados", antes,
            new Dictionary<string, string?> { ["nome"] = usuario.Nome, ["emailAlterado"] = emailMudou ? "sim" : "não" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok(emailMudou
            ? "Dados salvos. Como o e-mail mudou, as sessões abertas desse usuário foram encerradas; ele entra agora com o e-mail novo."
            : "Dados salvos.", usuario.Id);
    }

    public async Task<ResultadoGestao> DefinirSituacaoAsync(Guid tenantId, Guid usuarioId, bool ativo, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var (usuario, _, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        GarantirNaoEhOAtor(ator, usuario!.Id);
        if (!await PodeGerirIdentidadeAsync(ator, usuario, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return NaoPermitidoOutroContribuinte();
        }

        var agora = _relogio.AgoraUtc;
        if (ativo)
        {
            usuario.Ativar(agora);
        }
        else
        {
            usuario.Inativar(agora); // rotaciona a marca de segurança: as sessões caem
        }

        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, ativo ? AcaoAuditoria.UserEnabled : AcaoAuditoria.UserDisabled, usuario.Id,
            ativo ? "usuário ativado" : "usuário inativado (sessões encerradas)", cancellationToken: cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok(ativo ? "Usuário ativado." : "Usuário inativado: ele não consegue mais entrar e as sessões abertas foram encerradas.", usuario.Id);
    }

    public async Task<ResultadoGestao> DesbloquearAsync(Guid tenantId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var (usuario, _, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        GarantirNaoEhOAtor(ator, usuario!.Id);
        if (!await PodeGerirIdentidadeAsync(ator, usuario, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return NaoPermitidoOutroContribuinte();
        }

        usuario.Desbloquear(_relogio.AgoraUtc);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.UserUpdated, usuario.Id, "bloqueio por tentativas liberado", cancellationToken: cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok("Bloqueio liberado: o usuário já pode tentar entrar de novo.", usuario.Id);
    }

    /// <summary>Define os perfis do usuário NESTE contribuinte (substitui o conjunto atual).</summary>
    public async Task<ResultadoGestao> DefinirPerfisAsync(Guid tenantId, Guid usuarioId, IReadOnlyCollection<Guid> perfisInformados, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        GarantirNaoEhOAtor(ator, usuarioId);
        var (usuario, vinculo, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        if (!vinculo!.EstaAtivo)
        {
            return ResultadoGestao.Conflito("O acesso deste usuário ao contribuinte está revogado. Dê acesso de novo antes de escolher os perfis.");
        }

        var perfis = Distintos(perfisInformados);
        var problema = await ValidarPerfisAsync(tenantId, perfis, cancellationToken).ConfigureAwait(false);
        if (problema is not null)
        {
            return problema;
        }

        var atuais = await _repositorio.PerfisDoVinculoAsync(vinculo.Id, cancellationToken).ConfigureAwait(false);
        var incluidos = perfis.Except(atuais).ToList();
        var escalada = await VerificarEscaladaAsync(tenantId, ator, incluidos, cancellationToken).ConfigureAwait(false);
        if (escalada is not null)
        {
            return escalada;
        }

        await _repositorio.DefinirPerfisDoVinculoAsync(vinculo.Id, perfis, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.RoleAssigned, usuario!.Id, "perfis do usuário no contribuinte definidos",
            new Dictionary<string, string?> { ["perfis"] = string.Join(",", atuais) },
            new Dictionary<string, string?> { ["perfis"] = string.Join(",", perfis) }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok("Perfis salvos. Valem a partir da próxima ação do usuário.", usuario.Id);
    }

    /// <summary>
    /// Revoga o acesso do usuário a ESTE contribuinte (o vínculo e os perfis ficam, para devolver depois) e encerra as
    /// sessões dele — usado pela gestão de vários contribuintes. Já revogado: nada muda.
    /// </summary>
    internal async Task<ResultadoGestao> RevogarAcessoAsync(Guid tenantId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        GarantirNaoEhOAtor(ator, usuarioId);
        var (usuario, vinculo, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        if (!vinculo!.EstaAtivo)
        {
            return ResultadoGestao.Ok("O acesso já estava revogado.", usuario!.Id);
        }

        var agora = _relogio.AgoraUtc;
        vinculo.Revogar(agora);
        usuario!.RevogarSessoes(agora);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.AccessRevoked, usuario.Id, "acesso ao contribuinte revogado", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return ResultadoGestao.Ok("Acesso revogado.", usuario.Id);
    }

    /// <summary>
    /// Exclui o usuário DESTE contribuinte (remove o acesso e os perfis). Se ele não for de nenhum outro contribuinte,
    /// a conta também é inativada (nunca apagada: a trilha de auditoria continua apontando para ela).
    /// </summary>
    public async Task<ResultadoGestao> ExcluirAsync(Guid tenantId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        GarantirNaoEhOAtor(ator, usuarioId);
        var (usuario, vinculo, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        if (usuario!.AdministradorPlataforma && !await AtorEhAdministradorPlataformaAsync(ator, cancellationToken).ConfigureAwait(false))
        {
            return ResultadoGestao.NaoPermitido("Este usuário é Administrador da plataforma: só outro administrador da plataforma pode excluí-lo.");
        }

        var exclusivo = !usuario.AdministradorPlataforma
            && !await _repositorio.TemVinculoComOutroContribuinteAsync(usuario.Id, tenantId, cancellationToken).ConfigureAwait(false);
        var agora = _relogio.AgoraUtc;
        await _repositorio.RemoverVinculoAsync(vinculo!, cancellationToken).ConfigureAwait(false);
        if (exclusivo)
        {
            usuario.Inativar(agora);
        }

        usuario.RevogarSessoes(agora);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.TenantUserUnlinked, usuario.Id,
            exclusivo ? "usuário excluído do contribuinte e conta inativada" : "usuário excluído do contribuinte (continua em outros)",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok(exclusivo
            ? "Usuário excluído deste contribuinte. Como ele não tinha acesso a outro, a conta foi inativada."
            : "Usuário excluído deste contribuinte. Ele continua com acesso aos outros contribuintes dele.", usuario.Id);
    }

    /// <summary>Envia ao e-mail CADASTRADO do usuário um link para ele criar uma nova senha.</summary>
    public async Task<ResultadoGestao> EnviarLinkSenhaAsync(Guid tenantId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        GarantirNaoEhOAtor(ator, usuarioId);
        var (usuario, _, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        var resultado = await _recuperacao.EnviarLinkPeloAdministradorAsync(usuario!, novoUsuario: !usuario!.PossuiCredencial, cancellationToken).ConfigureAwait(false);
        return resultado switch
        {
            ResultadoEnvioLinkSenha.Enviado => ResultadoGestao.Ok($"Link enviado para {usuario.Email}.", usuario.Id),
            ResultadoEnvioLinkSenha.Indisponivel => ResultadoGestao.Conflito("O envio de e-mail não está configurado neste portal."),
            ResultadoEnvioLinkSenha.LimiteAtingido => ResultadoGestao.Conflito("Este usuário já recebeu o máximo de links na última hora. Tente mais tarde."),
            ResultadoEnvioLinkSenha.UsuarioIndisponivel => ResultadoGestao.Conflito("O usuário está inativo ou bloqueado: ative-o antes de enviar o link."),
            _ => ResultadoGestao.Conflito("O e-mail não pôde ser enviado agora. Tente de novo em instantes."),
        };
    }

    /// <summary>
    /// Gera uma SENHA TEMPORÁRIA forte para o usuário e a envia ao e-mail cadastrado dele; no próximo acesso ele é levado
    /// a criar a senha definitiva. A senha antiga deixa de valer e as sessões abertas caem. A senha nunca aparece na tela,
    /// em log ou na auditoria (só no e-mail). Mesmas regras de conta de "Alterar dados" (conta exclusiva deste
    /// contribuinte, ou quem age é administrador da plataforma).
    /// </summary>
    public async Task<ResultadoGestao> GerarSenhaTemporariaAsync(Guid tenantId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        GarantirNaoEhOAtor(ator, usuarioId);
        var (usuario, _, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        if (!await PodeGerirIdentidadeAsync(ator, usuario!, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return NaoPermitidoOutroContribuinte();
        }

        if (usuario!.Status != StatusUsuario.Ativo)
        {
            return ResultadoGestao.Conflito("O usuário está inativo ou bloqueado: ative-o antes de gerar a senha.");
        }

        if (!_recuperacao.Disponivel)
        {
            return ResultadoGestao.Conflito("O envio de e-mail não está configurado neste portal.");
        }

        var senha = GerarSenhaTemporariaValida(usuario.Login, usuario.Email);
        usuario.DefinirSenhaTemporaria(_hasher.Gerar(senha), _relogio.AgoraUtc);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.TemporaryPasswordIssued, usuario.Id, "senha temporária gerada e enviada por e-mail (troca obrigatória no próximo acesso)",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var enviada = await _recuperacao.EnviarSenhaTemporariaAsync(usuario, senha, cancellationToken).ConfigureAwait(false);
        return enviada
            ? ResultadoGestao.Ok($"Senha temporária enviada para {usuario.Email}. A senha anterior deixou de valer e as sessões abertas foram encerradas.", usuario.Id)
            : ResultadoGestao.Conflito("A senha foi trocada, mas o e-mail não pôde ser enviado agora. Gere outra senha temporária em instantes.");
    }

    /// <summary>
    /// Liga/desliga a exigência de MFA do usuário: exigido e ainda não cadastrado, o próximo login leva ao cadastro do
    /// autenticador (QR code) antes de qualquer tela. Configuração da CONTA: mesmas regras de "Alterar dados".
    /// </summary>
    public async Task<ResultadoGestao> DefinirMfaObrigatorioAsync(Guid tenantId, Guid usuarioId, bool obrigatorio, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        GarantirNaoEhOAtor(ator, usuarioId);
        var (usuario, _, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        if (!await PodeGerirIdentidadeAsync(ator, usuario!, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return NaoPermitidoOutroContribuinte();
        }

        var antes = usuario!.MfaObrigatorio;
        if (antes == obrigatorio)
        {
            return ResultadoGestao.Ok(obrigatorio ? "O MFA já era exigido deste usuário." : "O MFA já não era exigido deste usuário.", usuario.Id);
        }

        usuario.DefinirMfaObrigatorio(obrigatorio, _relogio.AgoraUtc);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.MfaRequirementChanged, usuario.Id, obrigatorio ? "MFA passou a ser exigido" : "MFA deixou de ser exigido",
            antes: new Dictionary<string, string?> { ["segundoFatorExigido"] = antes ? "sim" : "não" },
            novos: new Dictionary<string, string?> { ["segundoFatorExigido"] = obrigatorio ? "sim" : "não" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ResultadoGestao.Ok(obrigatorio
            ? (usuario.MfaHabilitado ? "MFA exigido. O usuário já tem o autenticador cadastrado." : "MFA exigido. No próximo acesso o usuário cadastra o autenticador (QR code).")
            : "O MFA deixou de ser exigido. Se o usuário já tem o autenticador cadastrado, ele continua sendo pedido.", usuario.Id);
    }

    /// <summary>
    /// REMOVE o MFA do usuário (perdeu o celular): apaga o segredo e os códigos de recuperação e encerra as sessões dele.
    /// Operação SENSÍVEL — exige a verificação em duas etapas de quem remove (step-up), nunca a si mesmo, e as regras de
    /// conta de "Alterar dados". Se o MFA for exigido, o usuário cadastra um novo autenticador no próximo acesso.
    /// </summary>
    /// <exception cref="MfaRequeridoException">Quem remove ainda não fez o MFA nesta sessão.</exception>
    public async Task<ResultadoGestao> RemoverMfaAsync(Guid tenantId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var ator = await ExigirAdministradorAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (!_seguranca.MfaSatisfeito)
        {
            throw new MfaRequeridoException();
        }

        GarantirNaoEhOAtor(ator, usuarioId);
        var (usuario, _, recusa) = await CarregarAlvoAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
        if (recusa is not null)
        {
            return recusa;
        }

        if (!await PodeGerirIdentidadeAsync(ator, usuario!, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return NaoPermitidoOutroContribuinte();
        }

        if (!usuario!.MfaHabilitado && string.IsNullOrEmpty(usuario.SegredoMfaProtegido))
        {
            return ResultadoGestao.Ok("O usuário não tem MFA cadastrado.", usuario.Id);
        }

        usuario.DesabilitarMfa(_relogio.AgoraUtc);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        var codigos = await _usuarios.RemoverCodigosRecuperacaoMfaAsync(usuario.Id, cancellationToken).ConfigureAwait(false);
        await AuditarAsync(tenantId, AcaoAuditoria.MfaRemovedByAdministrator, usuario.Id, "MFA removido pelo administrador (segredo e códigos de recuperação apagados; sessões encerradas)",
            novos: new Dictionary<string, string?> { ["codigosReservaApagados"] = codigos.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ResultadoGestao.Ok(usuario.MfaObrigatorio
            ? "MFA removido e sessões do usuário encerradas. Como o MFA é exigido, ele cadastra um novo autenticador no próximo acesso."
            : "MFA removido e sessões do usuário encerradas. Ele entra só com a senha até cadastrar de novo (Minha conta).", usuario.Id);
    }

    /// <summary>
    /// Concede ou remove o perfil "Administrador da plataforma" (acesso total, todos os contribuintes, sem MFA). Só um
    /// administrador da plataforma pode; nunca a si mesmo. A mudança derruba as sessões do usuário (marca de segurança).
    /// </summary>
    public async Task<ResultadoGestao> DefinirAdministradorPlataformaAsync(Guid usuarioId, bool administrador, CancellationToken cancellationToken = default)
    {
        await _plataforma.ExigirAsync(Permissoes.PlataformaAdministradoresGerir, cancellationToken).ConfigureAwait(false);
        var ator = AtorAtual();
        GarantirNaoEhOAtor(ator, usuarioId);

        var usuario = await _repositorio.ObterUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        if (usuario is null)
        {
            return ResultadoGestao.NaoEncontrado("Usuário não encontrado.");
        }

        if (administrador && usuario.Status != StatusUsuario.Ativo)
        {
            return ResultadoGestao.Conflito("Ative o usuário antes de torná-lo Administrador da plataforma.");
        }

        usuario.DefinirAdministradorPlataforma(administrador, _relogio.AgoraUtc);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);
        await _auditoria.RegistrarAsync(
            new EventoAuditoria(ator.ToString(), EscoposAuditoria.Plataforma,
                administrador ? AcaoAuditoria.PlatformAdminGranted : AcaoAuditoria.PlatformAdminRevoked, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = "Usuario",
                RecursoId = usuario.Id.ToString(),
                Motivo = administrador ? "perfil Administrador da plataforma concedido" : "perfil Administrador da plataforma removido",
            },
            cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.Ok(administrador
            ? "Perfil Administrador da plataforma concedido. Vale a partir do próximo login do usuário."
            : "Perfil Administrador da plataforma removido. As sessões do usuário foram encerradas.", usuario.Id);
    }

    // ------------------------------------------------------------------------------------------------- apoio

    private async Task<Guid> ExigirAdministradorAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new TenantContextAusenteException();
        }

        await _autorizacao.ExigirAdministrativoAsync(tenantId, Permissoes.AdminUsuarios, cancellationToken).ConfigureAwait(false);
        return AtorAtual();
    }

    private Guid AtorAtual()
        => Guid.TryParse(_seguranca.PrincipalId, out var ator) && ator != Guid.Empty ? ator : throw new NaoAutenticadoException();

    private static void GarantirNaoEhOAtor(Guid ator, Guid alvo)
    {
        if (ator == alvo)
        {
            throw new AutoConcessaoNaoPermitidaException();
        }
    }

    /// <summary>O alvo precisa ter vínculo (ativo ou revogado) com ESTE contribuinte — senão é invisível daqui (IDOR).</summary>
    private async Task<(Usuario? Usuario, UsuarioTenant? Vinculo, ResultadoGestao? Recusa)> CarregarAlvoAsync(
        Guid tenantId, Guid usuarioId, CancellationToken cancellationToken)
    {
        var vinculo = usuarioId == Guid.Empty ? null : await _repositorio.ObterVinculoAsync(usuarioId, tenantId, cancellationToken).ConfigureAwait(false);
        var usuario = vinculo is null ? null : await _repositorio.ObterUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false);
        return usuario is null
            ? (null, null, ResultadoGestao.NaoEncontrado("Usuário não encontrado neste contribuinte."))
            : (usuario, vinculo, null);
    }

    private async Task<bool> AtorEhAdministradorPlataformaAsync(Guid ator, CancellationToken cancellationToken)
        => (await _plataforma.AvaliarAsync(Permissoes.PlataformaAdministradoresGerir, cancellationToken).ConfigureAwait(false)).Permitido
           || await _usuarios.EhAdministradorPlataformaAsync(ator, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Nome, e-mail (o login!) e situação são da CONTA, que pode valer em outros contribuintes: só muda por aqui se a
    /// conta for exclusiva deste contribuinte e não for de um administrador da plataforma — ou se quem altera for
    /// administrador da plataforma. Sem isso, o administrador de um contribuinte poderia trocar o e-mail de um usuário de
    /// OUTRO e tomar a conta dele pelo "Esqueci a senha".
    /// </summary>
    private async Task<bool> PodeGerirIdentidadeAsync(Guid ator, Usuario alvo, Guid tenantId, CancellationToken cancellationToken)
    {
        if (await AtorEhAdministradorPlataformaAsync(ator, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return !alvo.AdministradorPlataforma
            && !await _repositorio.TemVinculoComOutroContribuinteAsync(alvo.Id, tenantId, cancellationToken).ConfigureAwait(false);
    }

    private static ResultadoGestao NaoPermitidoOutroContribuinte()
        => ResultadoGestao.NaoPermitido(
            "Este usuário também tem acesso a outros contribuintes (ou é Administrador da plataforma). Os dados da conta dele só podem ser alterados por um Administrador da plataforma.");

    private async Task<ResultadoGestao?> ValidarPerfisAsync(Guid tenantId, Guid[] perfis, CancellationToken cancellationToken)
    {
        if (perfis.Length == 0)
        {
            return ResultadoGestao.Invalido("Escolha pelo menos um perfil.");
        }

        if (perfis.Length > MaximoPerfisPorUsuario)
        {
            return ResultadoGestao.Invalido($"Escolha no máximo {MaximoPerfisPorUsuario} perfis.");
        }

        foreach (var id in perfis)
        {
            var perfil = await _repositorio.ObterPerfilAsync(id, cancellationToken).ConfigureAwait(false);
            if (perfil is null || (perfil.TenantId is Guid dono && dono != tenantId))
            {
                // Perfil de OUTRO contribuinte é tratado como inexistente (não vaza que existe).
                return ResultadoGestao.Invalido("Um dos perfis escolhidos não existe neste contribuinte. Atualize a página.");
            }
        }

        return null;
    }

    /// <summary>Anti-escalada: ninguém concede (via perfil) uma permissão que ele mesmo não tem neste contribuinte.</summary>
    private async Task<ResultadoGestao?> VerificarEscaladaAsync(Guid tenantId, Guid ator, IReadOnlyCollection<Guid> perfis, CancellationToken cancellationToken)
    {
        if (perfis.Count == 0)
        {
            return null;
        }

        var doAtor = await _usuarios.ObterPermissoesAsync(ator, tenantId, cancellationToken).ConfigureAwait(false);
        var concedidas = await _repositorio.ChavesDosPerfisAsync(perfis, cancellationToken).ConfigureAwait(false);
        var alem = concedidas.Where(c => !doAtor.Possui(c)).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (alem.Count == 0)
        {
            return null;
        }

        await AuditarAsync(tenantId, AcaoAuditoria.PrivilegeEscalationAttempt, ator, "tentativa de conceder permissões que o administrador não tem",
            resultado: ResultadoAuditoria.Negado, novos: new Dictionary<string, string?> { ["permissoes"] = string.Join(",", alem) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ResultadoGestao.NaoPermitido($"Você não pode conceder permissões que você mesmo não tem: {string.Join(", ", alem)}.");
    }

    // Senha temporária que cumpre a política (não contém o login nem o e-mail do usuário).
    private string GerarSenhaTemporariaValida(string login, string email)
    {
        var gerador = _geradorTokens ?? throw new InvalidOperationException("Gerador de senhas indisponível.");
        for (var tentativa = 0; tentativa < 20; tentativa++)
        {
            var senha = gerador.GerarSenhaTemporaria();
            if (PoliticaSenha.Validar(senha, login, email) is null)
            {
                return senha;
            }
        }

        throw new InvalidOperationException("Não foi possível gerar uma senha temporária válida.");
    }

    private static Guid[] Distintos(IReadOnlyCollection<Guid>? ids)
        => ids is null ? [] : [.. ids.Where(id => id != Guid.Empty).Distinct()];

    private static bool LoginValido(string login)
        => login.Length is >= TamanhoMinimoLogin and <= TamanhoMaximoLogin && PadraoLogin().IsMatch(login);

    [GeneratedRegex("^[A-Za-z0-9._@-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex PadraoLogin();

    private ValueTask AuditarAsync(
        Guid tenantId,
        string acao,
        Guid alvo,
        string motivo,
        IReadOnlyDictionary<string, string?>? antes = null,
        IReadOnlyDictionary<string, string?>? novos = null,
        ResultadoAuditoria resultado = ResultadoAuditoria.Permitido,
        CancellationToken cancellationToken = default)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", tenantId.ToString(), acao, resultado, _relogio.AgoraUtc)
            {
                TipoRecurso = "Usuario",
                RecursoId = alvo.ToString(),
                Motivo = motivo,
                ValoresAnteriores = antes,
                ValoresNovos = novos,
            },
            cancellationToken);
}
