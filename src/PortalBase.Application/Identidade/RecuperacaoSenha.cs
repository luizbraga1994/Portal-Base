using System.Globalization;
using System.Net;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Notificacoes;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Identidade;

/// <summary>
/// Parâmetros do "Esqueci a senha": endereço público do portal (base do link — configurado no servidor, nunca do
/// Host da requisição), validade do link e quantos links um mesmo usuário pode receber por hora (anti-abuso).
/// </summary>
public sealed record OpcoesRecuperacaoSenha(string UrlPublicaPortal, TimeSpan Validade, int MaximoPorHora, TimeSpan? ValidadeConvite = null)
{
    /// <summary>Validade do link de BOAS-VINDAS de um usuário recém-cadastrado (maior: a pessoa pode não ver na hora).</summary>
    public TimeSpan ValidadeConviteEfetiva => ValidadeConvite ?? TimeSpan.FromHours(24);

    /// <summary>
    /// Endereço VIGENTE na hora do envio. A configuração do e-mail muda pela tela, e no Blazor Server o escopo é o
    /// circuito inteiro: sem isto, o link saía com o endereço de quando a tela foi aberta (vazio ⇒ link relativo
    /// "/conta/redefinir-senha?…", que o leitor de e-mail não abre — bug de 08/10/2026).
    /// </summary>
    public Func<string>? UrlVigente { get; init; }

    /// <summary>O endereço a usar agora: o vigente, se houver fonte; senão, o fixo.</summary>
    public string UrlEfetiva => UrlVigente?.Invoke() ?? UrlPublicaPortal;

    public static OpcoesRecuperacaoSenha Padrao(string urlPublicaPortal) => new(urlPublicaPortal, TimeSpan.FromMinutes(30), 3, TimeSpan.FromHours(24));

    /// <summary>Padrões com o endereço lido da fonte a cada envio.</summary>
    public static OpcoesRecuperacaoSenha Vigente(Func<string> urlPublicaPortal)
    {
        ArgumentNullException.ThrowIfNull(urlPublicaPortal);
        return Padrao(string.Empty) with { UrlVigente = urlPublicaPortal };
    }
}

/// <summary>Resultado do envio de um link de senha pedido por um ADMINISTRADOR (novo usuário ou redefinição).</summary>
public enum ResultadoEnvioLinkSenha
{
    Enviado,

    /// <summary>O envio de e-mail está desligado no servidor.</summary>
    Indisponivel,

    /// <summary>O usuário já recebeu o máximo de links na última hora.</summary>
    LimiteAtingido,

    /// <summary>A fila de e-mails recusou (cheia ou desligada no momento).</summary>
    NaoEnfileirado,

    /// <summary>Usuário inativo ou bloqueado: não recebe link.</summary>
    UsuarioIndisponivel,
}

/// <summary>Resultado do pedido: só diz se o recurso está disponível — NUNCA se o e-mail existe (anti-enumeração).</summary>
public enum ResultadoSolicitacaoRecuperacao
{
    /// <summary>Pedido aceito. Se o e-mail for de um usuário ativo, um link foi enfileirado.</summary>
    Aceita,

    /// <summary>O envio de e-mail está desligado no servidor (vale para todos os usuários).</summary>
    Indisponivel,
}

public enum ResultadoRedefinicaoSenha
{
    Redefinida,

    /// <summary>Link inexistente, expirado ou já usado (resposta única — não diz qual).</summary>
    LinkInvalido,

    SenhasDiferentes,

    /// <summary>A nova senha não atende à <see cref="PoliticaSenha"/> (o link continua válido).</summary>
    SenhaForaDaPolitica,
}

/// <summary>
/// "Esqueci a senha" (seção 27). Pedido: resposta sempre igual (exista ou não o e-mail), link de uso único com
/// validade curta, guardado só como HASH (HMAC — <see cref="IHashDeterministico"/>), enviado ao e-mail CADASTRADO e
/// limitado por hora. Redefinição: o link é consumido de forma atômica, a senha nova segue a política, o bloqueio
/// por tentativas é limpo, TODAS as sessões abertas são revogadas (security stamp) e os demais links abertos são
/// invalidados. Tudo auditado; nunca registra token, link, senha ou e-mail.
/// </summary>
public sealed class RecuperacaoSenha
{
    private const int TamanhoMaximoToken = 512;

    private readonly IRepositorioUsuario _usuarios;
    private readonly IGeradorTokens _geradorTokens;
    private readonly IHashDeterministico _hash;
    private readonly IHasherSenha _hasher;
    private readonly IFilaEmail _fila;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;
    private readonly OpcoesRecuperacaoSenha _opcoes;

    public RecuperacaoSenha(
        IRepositorioUsuario usuarios,
        IGeradorTokens geradorTokens,
        IHashDeterministico hash,
        IHasherSenha hasher,
        IFilaEmail fila,
        IUnitOfWork unitOfWork,
        IAuditoria auditoria,
        IRelogio relogio,
        OpcoesRecuperacaoSenha opcoes)
    {
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(geradorTokens);
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(fila);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);

        _usuarios = usuarios;
        _geradorTokens = geradorTokens;
        _hash = hash;
        _hasher = hasher;
        _fila = fila;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _relogio = relogio;
        _opcoes = opcoes;
    }

    /// <summary>
    /// O envio de e-mail está ligado E há endereço do portal para o link (a tela mostra o formulário; senão, orienta a
    /// procurar o administrador). Sem endereço absoluto, nenhum link é enviado: um link relativo não abre no e-mail.
    /// </summary>
    public bool Disponivel => _fila.Habilitado && BasePortal() is not null;

    public async Task<ResultadoSolicitacaoRecuperacao> SolicitarAsync(string? email, CancellationToken cancellationToken = default)
    {
        if (!Disponivel)
        {
            return ResultadoSolicitacaoRecuperacao.Indisponivel;
        }

        var agora = _relogio.AgoraUtc;
        var usuario = string.IsNullOrWhiteSpace(email) || email.Trim().Length > Usuario.TamanhoMaximoEmail
            ? null
            : await _usuarios.ObterPorEmailAsync(email, cancellationToken).ConfigureAwait(false);

        // Desconhecido ou fora de uso: nada é enviado, e a resposta é a MESMA (anti-enumeração).
        if (usuario is null || usuario.Status != StatusUsuario.Ativo)
        {
            await AuditarAsync(usuario?.Id.ToString() ?? "desconhecido", AcaoAuditoria.PasswordRecoveryStarted, ResultadoAuditoria.Negado, "e-mail desconhecido ou usuário inativo", cancellationToken).ConfigureAwait(false);
            return ResultadoSolicitacaoRecuperacao.Aceita;
        }

        var recentes = await _usuarios.ContarTokensRecuperacaoDesdeAsync(usuario.Id, agora.AddHours(-1), cancellationToken).ConfigureAwait(false);
        if (recentes >= _opcoes.MaximoPorHora)
        {
            await AuditarAsync(usuario.Id.ToString(), AcaoAuditoria.PasswordRecoveryStarted, ResultadoAuditoria.Negado, "limite de links por hora", cancellationToken).ConfigureAwait(false);
            return ResultadoSolicitacaoRecuperacao.Aceita;
        }

        var token = _geradorTokens.GerarToken();
        var hashToken = await _hash.HashAsync(token, cancellationToken).ConfigureAwait(false);
        await _usuarios.AdicionarTokenRecuperacaoAsync(TokenRecuperacaoConta.Emitir(usuario.Id, hashToken, agora, _opcoes.Validade), cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);

        var enfileirado = _fila.Enfileirar(MontarEmail(usuario, token));
        await AuditarAsync(
            usuario.Id.ToString(),
            AcaoAuditoria.PasswordRecoveryStarted,
            enfileirado ? ResultadoAuditoria.Permitido : ResultadoAuditoria.Negado,
            enfileirado ? "link enfileirado para envio" : "e-mail não enfileirado (fila cheia ou envio desligado)",
            cancellationToken).ConfigureAwait(false);
        return ResultadoSolicitacaoRecuperacao.Aceita;
    }

    /// <summary>
    /// Link de senha pedido por um ADMINISTRADOR (a autorização é do caso de uso que chama): boas-vindas de um usuário
    /// recém-cadastrado (validade maior) ou redefinição. Vai sempre para o e-mail CADASTRADO; respeita o limite por hora;
    /// guarda só o hash; auditado. Nunca devolve o token.
    /// </summary>
    public async Task<ResultadoEnvioLinkSenha> EnviarLinkPeloAdministradorAsync(Usuario usuario, bool novoUsuario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (!Disponivel)
        {
            return ResultadoEnvioLinkSenha.Indisponivel;
        }

        var agora = _relogio.AgoraUtc;
        // Inativo/bloqueado não recebe link (o bloqueio TEMPORÁRIO por tentativas não impede: a redefinição o limpa).
        if (usuario.Status != StatusUsuario.Ativo)
        {
            return ResultadoEnvioLinkSenha.UsuarioIndisponivel;
        }

        var recentes = await _usuarios.ContarTokensRecuperacaoDesdeAsync(usuario.Id, agora.AddHours(-1), cancellationToken).ConfigureAwait(false);
        if (recentes >= _opcoes.MaximoPorHora)
        {
            await AuditarAsync(usuario.Id.ToString(), AcaoAuditoria.PasswordRecoveryStarted, ResultadoAuditoria.Negado, "link pelo administrador: limite por hora", cancellationToken).ConfigureAwait(false);
            return ResultadoEnvioLinkSenha.LimiteAtingido;
        }

        var validade = novoUsuario ? _opcoes.ValidadeConviteEfetiva : _opcoes.Validade;
        var token = _geradorTokens.GerarToken();
        var hashToken = await _hash.HashAsync(token, cancellationToken).ConfigureAwait(false);
        await _usuarios.AdicionarTokenRecuperacaoAsync(TokenRecuperacaoConta.Emitir(usuario.Id, hashToken, agora, validade), cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);

        var enfileirado = _fila.Enfileirar(novoUsuario ? MontarEmailBoasVindas(usuario, token, validade) : MontarEmail(usuario, token, peloAdministrador: true));
        await AuditarAsync(
            usuario.Id.ToString(),
            AcaoAuditoria.PasswordRecoveryStarted,
            enfileirado ? ResultadoAuditoria.Permitido : ResultadoAuditoria.Negado,
            enfileirado ? (novoUsuario ? "boas-vindas: link para criar a senha enfileirado" : "link pelo administrador enfileirado") : "e-mail não enfileirado",
            cancellationToken).ConfigureAwait(false);
        return enfileirado ? ResultadoEnvioLinkSenha.Enviado : ResultadoEnvioLinkSenha.NaoEnfileirado;
    }

    /// <summary>O link ainda serve? (a tela mostra o formulário ou a mensagem de link inválido, sem consumi-lo).</summary>
    public async Task<bool> LinkValidoAsync(string? token, CancellationToken cancellationToken = default)
        => await ObterTokenValidoAsync(token, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<ResultadoRedefinicaoSenha> RedefinirAsync(string? token, string? novaSenha, string? confirmacao, CancellationToken cancellationToken = default)
    {
        var registro = await ObterTokenValidoAsync(token, cancellationToken).ConfigureAwait(false);
        var usuario = registro is null ? null : await _usuarios.ObterPorIdAsync(registro.UsuarioId, cancellationToken).ConfigureAwait(false);
        if (registro is null || usuario is null || usuario.Status != StatusUsuario.Ativo)
        {
            await AuditarAsync(usuario?.Id.ToString() ?? "desconhecido", AcaoAuditoria.PasswordRecoveryCompleted, ResultadoAuditoria.Negado, "link inválido, expirado ou usado", cancellationToken).ConfigureAwait(false);
            return ResultadoRedefinicaoSenha.LinkInvalido;
        }

        if (!string.Equals(novaSenha, confirmacao, StringComparison.Ordinal))
        {
            return ResultadoRedefinicaoSenha.SenhasDiferentes;
        }

        if (PoliticaSenha.Validar(novaSenha, usuario.Login, usuario.Email) is not null)
        {
            return ResultadoRedefinicaoSenha.SenhaForaDaPolitica;
        }

        var agora = _relogio.AgoraUtc;
        var consumido = false;
        await _unitOfWork.ExecutarEmTransacaoAsync(
            async ct =>
            {
                consumido = await _usuarios.ConsumirTokenRecuperacaoAsync(registro.Id, agora, ct).ConfigureAwait(false);
                if (!consumido)
                {
                    return; // outro pedido usou o link primeiro
                }

                usuario.DefinirCredencial(_hasher.Gerar(novaSenha!), agora); // limpa o bloqueio e revoga as sessões
                await _unitOfWork.SalvarAlteracoesAsync(ct).ConfigureAwait(false);
                await _usuarios.InvalidarTokensRecuperacaoAsync(usuario.Id, agora, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (!consumido)
        {
            await AuditarAsync(usuario.Id.ToString(), AcaoAuditoria.PasswordRecoveryCompleted, ResultadoAuditoria.Negado, "link já usado", cancellationToken).ConfigureAwait(false);
            return ResultadoRedefinicaoSenha.LinkInvalido;
        }

        await AuditarAsync(usuario.Id.ToString(), AcaoAuditoria.PasswordRecoveryCompleted, ResultadoAuditoria.Permitido, "senha redefinida; sessões revogadas", cancellationToken).ConfigureAwait(false);
        return ResultadoRedefinicaoSenha.Redefinida;
    }

    private async Task<TokenRecuperacaoConta?> ObterTokenValidoAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > TamanhoMaximoToken)
        {
            return null;
        }

        var hashToken = await _hash.HashAsync(token.Trim(), cancellationToken).ConfigureAwait(false);
        var registro = await _usuarios.ObterTokenRecuperacaoAsync(hashToken, cancellationToken).ConfigureAwait(false);
        return registro is not null && registro.EstaValido(_relogio.AgoraUtc) ? registro : null;
    }

    private string Link(string token) => $"{BasePortalExigida()}/conta/redefinir-senha?token={Uri.EscapeDataString(token)}";

    /// <summary>Endereço absoluto (http/https) do portal, sem a barra final — ou nulo se não houver um válido.</summary>
    private string? BasePortal()
    {
        var url = _opcoes.UrlEfetiva?.Trim().TrimEnd('/');
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? url
            : null;
    }

    private string BasePortalExigida()
        => BasePortal() ?? throw new InvalidOperationException("Endereço público do portal ausente: o link não pode ser montado.");

    /// <summary>
    /// Enfileira o e-mail com a SENHA TEMPORÁRIA gerada pelo administrador (o próprio caso de uso gerou e gravou a
    /// senha). A senha só existe na mensagem — nunca em log, banco ou auditoria (regra 8). <c>false</c>: e-mail desligado
    /// ou fila cheia.
    /// </summary>
    public async Task<bool> EnviarSenhaTemporariaAsync(Usuario usuario, string senhaTemporaria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        ArgumentException.ThrowIfNullOrEmpty(senhaTemporaria);
        if (!Disponivel)
        {
            return false;
        }

        var entrar = $"{BasePortalExigida()}/conta/entrar";
        var nome = string.IsNullOrWhiteSpace(usuario.Nome) ? "Olá" : $"Olá, {usuario.Nome}";
        var texto =
            $"{nome}.\n\n" +
            "Um administrador do Portal Base gerou uma senha temporária para você:\n\n" +
            $"    {senhaTemporaria}\n\n" +
            $"Entre em {entrar} com este e-mail ({usuario.Email}) e essa senha. No primeiro acesso o portal pede que você crie uma senha nova (a temporária deixa de valer).\n\n" +
            "Se você não esperava este e-mail, avise o administrador do portal.\n";
        var html =
            $"<p>{WebUtility.HtmlEncode(nome)}.</p>" +
            "<p>Um administrador do <strong>Portal Base</strong> gerou uma senha temporária para você:</p>" +
            $"<p data-destaque><strong>{WebUtility.HtmlEncode(senhaTemporaria)}</strong></p>" +
            $"<p>Entre no portal com este e-mail ({WebUtility.HtmlEncode(usuario.Email)}) e essa senha. No primeiro acesso o portal pede que você crie uma senha nova (a temporária deixa de valer).</p>" +
            $"<a data-acao href=\"{WebUtility.HtmlEncode(entrar)}\">Entrar no Portal Base</a>" +
            "<p>Se você não esperava este e-mail, avise o administrador do portal.</p>";

        var enfileirado = _fila.Enfileirar(new MensagemEmail(usuario.Email, "Sua senha temporária — Portal Base", texto, html));
        await AuditarAsync(
            usuario.Id.ToString(),
            AcaoAuditoria.PasswordRecoveryStarted,
            enfileirado ? ResultadoAuditoria.Permitido : ResultadoAuditoria.Negado,
            enfileirado ? "senha temporária enviada por e-mail" : "e-mail com a senha temporária não enfileirado",
            cancellationToken).ConfigureAwait(false);
        return enfileirado;
    }

    private MensagemEmail MontarEmailBoasVindas(Usuario usuario, string token, TimeSpan validade)
    {
        var link = Link(token);
        var horas = ((int)validade.TotalHours).ToString(CultureInfo.InvariantCulture);
        var nome = string.IsNullOrWhiteSpace(usuario.Nome) ? "Olá" : $"Olá, {usuario.Nome}";

        var texto =
            $"{nome}.\n\n" +
            "Você recebeu acesso ao Portal Base. Para entrar, crie a sua senha pelo link abaixo:\n\n" +
            $"{link}\n\n" +
            $"O link vale por {horas} horas e só pode ser usado uma vez. Depois, entre no portal com este e-mail ({usuario.Email}) e a senha criada.\n\n" +
            "Se você não esperava este acesso, ignore este e-mail.\n";

        var html =
            $"<p>{WebUtility.HtmlEncode(nome)}.</p>" +
            "<p>Você recebeu acesso ao <strong>Portal Base</strong>. Para entrar, crie a sua senha:</p>" +
            $"<a data-acao href=\"{WebUtility.HtmlEncode(link)}\">Criar minha senha</a>" +
            $"<p>O link vale por {horas} horas e só pode ser usado uma vez. Depois, entre no portal com este e-mail ({WebUtility.HtmlEncode(usuario.Email)}) e a senha criada.</p>" +
            "<p>Se você não esperava este acesso, ignore este e-mail.</p>";

        return new MensagemEmail(usuario.Email, "Seu acesso ao Portal Base — crie sua senha", texto, html);
    }

    private MensagemEmail MontarEmail(Usuario usuario, string token, bool peloAdministrador = false)
    {
        var link = Link(token);
        var minutos = ((int)_opcoes.Validade.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        var nome = string.IsNullOrWhiteSpace(usuario.Nome) ? "Olá" : $"Olá, {usuario.Nome}";
        var pedido = peloAdministrador
            ? "Um administrador do Portal Base enviou a você um link para criar uma nova senha."
            : "Recebemos um pedido para redefinir a sua senha do Portal Base.";

        var texto =
            $"{nome}.\n\n" +
            $"{pedido} Para criar uma nova senha, abra o link abaixo:\n\n" +
            $"{link}\n\n" +
            $"O link vale por {minutos} minutos e só pode ser usado uma vez. Ao redefinir, as sessões abertas são encerradas em até 1 minuto.\n\n" +
            "Se não foi você quem pediu, ignore este e-mail: a sua senha atual continua valendo.\n";

        var html =
            $"<p>{WebUtility.HtmlEncode(nome)}.</p>" +
            $"<p>{WebUtility.HtmlEncode(pedido)}</p>" +
            $"<a data-acao href=\"{WebUtility.HtmlEncode(link)}\">Criar uma nova senha</a>" +
            $"<p>O link vale por {minutos} minutos e só pode ser usado uma vez. Ao redefinir, as sessões abertas são encerradas em até 1 minuto.</p>" +
            "<p>Se não foi você quem pediu, ignore este e-mail: a sua senha atual continua valendo.</p>";

        return new MensagemEmail(usuario.Email, "Redefinição de senha — Portal Base", texto, html);
    }

    private ValueTask AuditarAsync(string principal, string operacao, ResultadoAuditoria resultado, string motivo, CancellationToken cancellationToken)
        => _auditoria.RegistrarAsync(
            new EventoAuditoria(principal, EscoposAuditoria.Identidade, operacao, resultado, _relogio.AgoraUtc)
            {
                TipoRecurso = "Usuario",
                RecursoId = principal,
                Motivo = motivo,
            },
            cancellationToken);
}
