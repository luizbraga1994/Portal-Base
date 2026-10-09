using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;

namespace PortalBase.Application.Notificacoes;

/// <summary>De onde vem a configuração de e-mail em vigor.</summary>
public enum OrigemConfiguracaoEmail
{
    /// <summary>Seção <c>Email</c> do servidor (appsettings / user-secrets / variáveis).</summary>
    Servidor = 0,

    /// <summary>Definida pela tela Configurações → E-mail (catálogo + cofre).</summary>
    Tela = 1,
}

/// <summary>A configuração de e-mail em vigor, como a tela mostra (nunca a senha: só se ela está definida).</summary>
public sealed record ConfiguracaoEmailAtual(
    OrigemConfiguracaoEmail Origem,
    string Modo,
    bool Habilitado,
    string Servidor,
    int Porta,
    string Seguranca,
    string Usuario,
    bool SenhaDefinida,
    string RemetenteEmail,
    string RemetenteNome,
    string UrlPublicaPortal,
    DateTimeOffset? AtualizadoEmUtc,
    string Tema = PortalBase.Domain.Notificacoes.TemasEmail.Padrao,
    string CorDestaque = "",
    string TextoRodape = "");

/// <summary>Aparência dos e-mails escolhida na tela: tema, cor de destaque (#RRGGBB, opcional) e texto do rodapé.</summary>
public sealed record AlteracaoAparenciaEmail(string? Tema, string? CorDestaque, string? TextoRodape);

/// <summary>O que o administrador digitou na tela. <see cref="NovaSenha"/> vazia = manter a senha atual.</summary>
public sealed record AlteracaoConfiguracaoEmail(
    bool Habilitado,
    string? Servidor,
    int Porta,
    string? Seguranca,
    string? Usuario,
    string? NovaSenha,
    string? RemetenteEmail,
    string? RemetenteNome,
    string? UrlPublicaPortal);

/// <summary>Desfecho de salvar/testar: <see cref="Erros"/> em linguagem de negócio (nunca segredo).</summary>
public sealed record ResultadoConfiguracaoEmail(bool Sucesso, IReadOnlyList<string> Erros)
{
    public static readonly ResultadoConfiguracaoEmail Ok = new(true, []);

    public static ResultadoConfiguracaoEmail Falha(params string[] erros) => new(false, erros);
}

/// <summary>
/// Acesso à configuração de e-mail (Infraestrutura): leitura do que vale agora, validação com as MESMAS regras da
/// configuração do servidor, gravação da parte não secreta (catálogo) e envio de teste. A senha nunca passa por aqui:
/// vai para o cofre pelo caso de uso, com o nome <see cref="NomeSegredoSenha"/>.
/// </summary>
public interface IConfiguracaoEmailPortal
{
    /// <summary>Nome lógico (cofre) da senha do SMTP definida pela tela — nunca a senha.</summary>
    string NomeSegredoSenha { get; }

    Task<ConfiguracaoEmailAtual> ObterAsync(CancellationToken cancellationToken = default);

    /// <summary>Problemas da alteração (vazio = válida). <paramref name="senhaDefinida"/>: já há senha no cofre.</summary>
    IReadOnlyList<string> Validar(AlteracaoConfiguracaoEmail alteracao, bool senhaDefinida);

    /// <summary>Grava a parte NÃO secreta e passa a valer na hora (neste processo; nos outros, em até 1 minuto).</summary>
    Task SalvarAsync(AlteracaoConfiguracaoEmail alteracao, Guid? atualizadoPor, CancellationToken cancellationToken = default);

    /// <summary>Apaga a configuração da tela: volta a valer a do servidor.</summary>
    Task RemoverAsync(CancellationToken cancellationToken = default);

    /// <summary>Problemas da aparência (vazio = válida): tema conhecido, cor #RRGGBB, rodapé dentro do limite.</summary>
    IReadOnlyList<string> ValidarAparencia(AlteracaoAparenciaEmail aparencia);

    /// <summary>Grava a aparência na configuração da tela (que precisa existir) e passa a valer no próximo envio.</summary>
    Task SalvarAparenciaAsync(AlteracaoAparenciaEmail aparencia, Guid? atualizadoPor, CancellationToken cancellationToken = default);

    /// <summary>HTML de um e-mail de exemplo com a aparência informada (para a pré-visualização da tela).</summary>
    string Previa(AlteracaoAparenciaEmail aparencia, string? urlPublicaPortal);

    /// <summary>Endereço aceito como destinatário (formato simples, sem injeção de cabeçalho)?</summary>
    bool EnderecoValido(string? email);

    /// <summary>Envia AGORA um e-mail de teste com a configuração em vigor. Nulo = enviado; senão, o motivo (de negócio).</summary>
    Task<string?> EnviarTesteAsync(string para, CancellationToken cancellationToken = default);
}

/// <summary>
/// Configurações → E-mail (SMTP): o Administrador da plataforma define pela tela o servidor de e-mail do portal (o
/// mesmo para todos os contribuintes). Fail-closed: permissão de PLATAFORMA <see cref="Permissoes.PlataformaEmailAdministrar"/>
/// decidida no servidor antes de ler ou gravar; validação com as regras da configuração do servidor (TLS obrigatório
/// fora de Development, https, remetente válido...); a senha vai SÓ para o cofre (regra 7) e nunca volta para a tela;
/// toda alteração e todo teste são auditados (antes/depois sem segredo — regra 9).
/// </summary>
public sealed class ConfiguracaoEmailPlataforma
{
    private const string Recurso = "ConfiguracaoEmail";

    private readonly IAutorizacaoPlataforma _plataforma;
    private readonly IContextoSeguranca _seguranca;
    private readonly IConfiguracaoEmailPortal _portal;
    private readonly IEscritorSegredos _segredos;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public ConfiguracaoEmailPlataforma(
        IAutorizacaoPlataforma plataforma,
        IContextoSeguranca seguranca,
        IConfiguracaoEmailPortal portal,
        IEscritorSegredos segredos,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(plataforma);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _plataforma = plataforma;
        _seguranca = seguranca;
        _portal = portal;
        _segredos = segredos;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    /// <exception cref="AutorizacaoNegadaException">Sem a permissão de plataforma.</exception>
    public async Task<ConfiguracaoEmailAtual> ObterAsync(CancellationToken cancellationToken = default)
    {
        await _plataforma.ExigirAsync(Permissoes.PlataformaEmailAdministrar, cancellationToken).ConfigureAwait(false);
        return await _portal.ObterAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <exception cref="AutorizacaoNegadaException">Sem a permissão de plataforma.</exception>
    public async Task<ResultadoConfiguracaoEmail> SalvarAsync(AlteracaoConfiguracaoEmail alteracao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alteracao);
        await _plataforma.ExigirAsync(Permissoes.PlataformaEmailAdministrar, cancellationToken).ConfigureAwait(false);

        var antes = await _portal.ObterAsync(cancellationToken).ConfigureAwait(false);
        var senhaJaDefinida = antes.Origem == OrigemConfiguracaoEmail.Tela && antes.SenhaDefinida;
        var erros = _portal.Validar(alteracao, senhaJaDefinida);
        if (erros.Count > 0)
        {
            return new ResultadoConfiguracaoEmail(false, erros);
        }

        var novaSenha = string.IsNullOrEmpty(alteracao.NovaSenha) ? null : alteracao.NovaSenha;
        if (novaSenha is not null)
        {
            // Só no cofre (regra 7) — nunca no catálogo, em log ou na auditoria.
            await _segredos.DefinirAsync(_portal.NomeSegredoSenha, novaSenha, cancellationToken).ConfigureAwait(false);
        }

        await _portal.SalvarAsync(alteracao with { NovaSenha = null }, IdDoAtor(), cancellationToken).ConfigureAwait(false);
        var depois = await _portal.ObterAsync(cancellationToken).ConfigureAwait(false);

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", "GLOBAL", AcaoAuditoria.EmailConfigurationUpdated, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = Recurso,
                ValoresAnteriores = Campos(antes, senha: antes.SenhaDefinida ? "definida" : "não definida"),
                ValoresNovos = Campos(depois, senha: novaSenha is not null ? "alterada" : (depois.SenhaDefinida ? "mantida" : "não definida")),
            },
            cancellationToken).ConfigureAwait(false);

        return ResultadoConfiguracaoEmail.Ok;
    }

    /// <summary>
    /// Troca a aparência dos e-mails (tema, cor de destaque, rodapé). Só sobre a configuração da TELA: com a do servidor
    /// em vigor, pede para salvar o envio nesta tela antes (a aparência fica junto dela). Auditado (antes/depois).
    /// </summary>
    /// <exception cref="AutorizacaoNegadaException">Sem a permissão de plataforma.</exception>
    public async Task<ResultadoConfiguracaoEmail> SalvarAparenciaAsync(AlteracaoAparenciaEmail aparencia, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aparencia);
        await _plataforma.ExigirAsync(Permissoes.PlataformaEmailAdministrar, cancellationToken).ConfigureAwait(false);
        var antes = await _portal.ObterAsync(cancellationToken).ConfigureAwait(false);
        if (antes.Origem != OrigemConfiguracaoEmail.Tela)
        {
            return ResultadoConfiguracaoEmail.Falha(
                "Salve primeiro o envio (SMTP) nesta tela: a aparência dos e-mails fica guardada junto dessa configuração.");
        }

        var erros = _portal.ValidarAparencia(aparencia);
        if (erros.Count > 0)
        {
            return new ResultadoConfiguracaoEmail(false, erros);
        }

        await _portal.SalvarAparenciaAsync(aparencia, IdDoAtor(), cancellationToken).ConfigureAwait(false);
        var depois = await _portal.ObterAsync(cancellationToken).ConfigureAwait(false);
        await _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", "GLOBAL", AcaoAuditoria.EmailConfigurationUpdated, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = Recurso,
                Motivo = "Aparência dos e-mails alterada.",
                ValoresAnteriores = Aparencia(antes),
                ValoresNovos = Aparencia(depois),
            },
            cancellationToken).ConfigureAwait(false);
        return ResultadoConfiguracaoEmail.Ok;
    }

    /// <summary>Pré-visualização (HTML de exemplo) da aparência informada, com o endereço do portal em vigor.</summary>
    /// <exception cref="AutorizacaoNegadaException">Sem a permissão de plataforma.</exception>
    public async Task<string> PreviaAsync(AlteracaoAparenciaEmail aparencia, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aparencia);
        await _plataforma.ExigirAsync(Permissoes.PlataformaEmailAdministrar, cancellationToken).ConfigureAwait(false);
        var atual = await _portal.ObterAsync(cancellationToken).ConfigureAwait(false);
        return _portal.Previa(aparencia, atual.UrlPublicaPortal);
    }

    /// <summary>Volta a valer a configuração do servidor (apaga a da tela; a senha no cofre não é usada).</summary>
    /// <exception cref="AutorizacaoNegadaException">Sem a permissão de plataforma.</exception>
    public async Task RestaurarConfiguracaoDoServidorAsync(CancellationToken cancellationToken = default)
    {
        await _plataforma.ExigirAsync(Permissoes.PlataformaEmailAdministrar, cancellationToken).ConfigureAwait(false);
        var antes = await _portal.ObterAsync(cancellationToken).ConfigureAwait(false);
        await _portal.RemoverAsync(cancellationToken).ConfigureAwait(false);
        var depois = await _portal.ObterAsync(cancellationToken).ConfigureAwait(false);

        await _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", "GLOBAL", AcaoAuditoria.EmailConfigurationUpdated, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = Recurso,
                Motivo = "Configuração da tela removida; vale a configuração do servidor.",
                ValoresAnteriores = Campos(antes, senha: antes.SenhaDefinida ? "definida" : "não definida"),
                ValoresNovos = Campos(depois, senha: depois.SenhaDefinida ? "definida" : "não definida"),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Envia agora um e-mail de teste com a configuração em vigor.</summary>
    /// <exception cref="AutorizacaoNegadaException">Sem a permissão de plataforma.</exception>
    public async Task<ResultadoConfiguracaoEmail> TestarAsync(string? para, CancellationToken cancellationToken = default)
    {
        await _plataforma.ExigirAsync(Permissoes.PlataformaEmailAdministrar, cancellationToken).ConfigureAwait(false);
        if (para is null || !_portal.EnderecoValido(para))
        {
            return ResultadoConfiguracaoEmail.Falha("Informe um endereço de e-mail válido para receber o teste.");
        }

        var destino = para.Trim();

        var atual = await _portal.ObterAsync(cancellationToken).ConfigureAwait(false);
        if (!atual.Habilitado)
        {
            return ResultadoConfiguracaoEmail.Falha("O envio de e-mail está desligado. Ligue-o e salve antes de testar.");
        }

        var erro = await _portal.EnviarTesteAsync(destino, cancellationToken).ConfigureAwait(false);

        // O destinatário não vai para a trilha (dado pessoal); só o domínio, para diagnóstico.
        var dominio = destino[(destino.LastIndexOf('@') + 1)..];
        await _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", "GLOBAL", AcaoAuditoria.EmailTestSent,
                erro is null ? ResultadoAuditoria.Permitido : ResultadoAuditoria.Negado, _relogio.AgoraUtc)
            {
                TipoRecurso = Recurso,
                Motivo = erro is null ? $"Enviado para um endereço de {dominio}." : $"Falhou ({dominio}): {erro}",
            },
            cancellationToken).ConfigureAwait(false);

        return erro is null ? ResultadoConfiguracaoEmail.Ok : ResultadoConfiguracaoEmail.Falha(erro);
    }

    private static Dictionary<string, string?> Aparencia(ConfiguracaoEmailAtual c) => new(StringComparer.Ordinal)
    {
        ["tema"] = c.Tema,
        ["corDestaque"] = c.CorDestaque,
        ["textoRodape"] = c.TextoRodape,
    };

    private Guid? IdDoAtor() => Guid.TryParse(_seguranca.PrincipalId, out var id) ? id : null;

    private static Dictionary<string, string?> Campos(ConfiguracaoEmailAtual c, string senha) => new(StringComparer.Ordinal)
    {
        ["origem"] = c.Origem.ToString(),
        ["modo"] = c.Modo,
        ["servidor"] = c.Servidor,
        ["porta"] = c.Porta.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["seguranca"] = c.Seguranca,
        ["usuario"] = c.Usuario,
        ["senha"] = senha,
        ["remetente"] = c.RemetenteEmail,
        ["remetenteNome"] = c.RemetenteNome,
        ["urlPublicaPortal"] = c.UrlPublicaPortal,
    };
}
