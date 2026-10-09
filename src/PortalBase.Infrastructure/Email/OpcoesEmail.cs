using MimeKit;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// Configuração do e-mail transacional (seção <c>Email</c>). Só dados NÃO sensíveis: a senha do SMTP fica no cofre e
/// aqui vai apenas a REFERÊNCIA (<see cref="OpcoesSmtp.SenhaRef"/>) — regra 7.
/// </summary>
public sealed class OpcoesEmail
{
    public const string Secao = "Email";

    public const string ModoDesligado = "Desligado";
    public const string ModoSmtp = "Smtp";
    public const string ModoPastaLocal = "PastaLocal";

    /// <summary><c>Smtp</c>, <c>PastaLocal</c> (só Development: grava .eml numa pasta) ou <c>Desligado</c> (padrão).</summary>
    public string Modo { get; set; } = ModoDesligado;

    /// <summary>
    /// Endereço público do portal, base dos links enviados por e-mail (ex.: https://portal.empresa.com.br). NUNCA vem
    /// do cabeçalho Host da requisição: um Host forjado mandaria o link de redefinição para outro site.
    /// </summary>
    public string UrlPublicaPortal { get; set; } = string.Empty;

    public OpcoesRemetente Remetente { get; set; } = new();

    /// <summary>Aparência dos e-mails (tema, cor de destaque, texto do rodapé). A da tela, quando existe, vale no lugar desta.</summary>
    public OpcoesAparenciaEmail Aparencia { get; set; } = new();

    public OpcoesSmtp Smtp { get; set; } = new();

    public OpcoesPastaLocal PastaLocal { get; set; } = new();

    public bool Habilitado => !string.Equals(Modo, ModoDesligado, StringComparison.Ordinal);
}

public sealed class OpcoesAparenciaEmail
{
    /// <summary>Código de <see cref="PortalBase.Domain.Notificacoes.TemasEmail"/> (padrão: o layout do portal).</summary>
    public string Tema { get; set; } = PortalBase.Domain.Notificacoes.TemasEmail.Padrao;

    /// <summary>#RRGGBB no lugar da cor de destaque do tema; vazio = a do tema.</summary>
    public string CorDestaque { get; set; } = string.Empty;

    /// <summary>Texto livre do rodapé (ex.: empresa e telefone); vazio = só o padrão.</summary>
    public string TextoRodape { get; set; } = string.Empty;
}

public sealed class OpcoesRemetente
{
    public string Email { get; set; } = string.Empty;

    public string Nome { get; set; } = "Portal Base";
}

public sealed class OpcoesSmtp
{
    public const string SegurancaStartTls = "StartTls";
    public const string SegurancaSslTls = "SslTls";
    public const string SegurancaNenhuma = "Nenhuma";

    public string Servidor { get; set; } = string.Empty;

    public int Porta { get; set; } = 587;

    /// <summary><c>StartTls</c> (padrão; exige que o servidor ofereça STARTTLS), <c>SslTls</c> (TLS implícito, ex.: 465) ou <c>Nenhuma</c> (só Development).</summary>
    public string Seguranca { get; set; } = SegurancaStartTls;

    /// <summary>Usuário da autenticação SMTP. Vazio ⇒ sem autenticação (relay interno).</summary>
    public string Usuario { get; set; } = string.Empty;

    /// <summary>REFERÊNCIA da senha no cofre (Segredos:&lt;ref&gt; em Development) — nunca a senha.</summary>
    public string SenhaRef { get; set; } = "email-smtp-senha";

    public int TimeoutSegundos { get; set; } = 30;

    public int TentativasMaximas { get; set; } = 3;
}

public sealed class OpcoesPastaLocal
{
    /// <summary>Pasta (absoluta) onde os e-mails viram arquivos .eml — só Development.</summary>
    public string Caminho { get; set; } = string.Empty;
}

/// <summary>Validação fail-fast da configuração de e-mail (no startup do host).</summary>
public static class ValidadorOpcoesEmail
{
    /// <summary>Problemas encontrados (vazio = válida). Mensagens sem segredo (só nomes de chave e regras).</summary>
    public static IReadOnlyList<string> Validar(OpcoesEmail opcoes, bool desenvolvimento)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        var erros = new List<string>();

        if (opcoes.Modo is not (OpcoesEmail.ModoDesligado or OpcoesEmail.ModoSmtp or OpcoesEmail.ModoPastaLocal))
        {
            erros.Add($"Email:Modo deve ser {OpcoesEmail.ModoSmtp}, {OpcoesEmail.ModoPastaLocal} ou {OpcoesEmail.ModoDesligado}.");
            return erros;
        }

        if (!opcoes.Habilitado)
        {
            return erros;
        }

        if (!Uri.TryCreate(opcoes.UrlPublicaPortal, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && !(desenvolvimento && url.Scheme == Uri.UriSchemeHttp))
            || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            erros.Add(desenvolvimento
                ? "Email:UrlPublicaPortal deve ser o endereço do portal (http:// ou https://, sem ? nem #)."
                : "Email:UrlPublicaPortal deve ser o endereço https:// do portal (sem ? nem #).");
        }

        if (!EnderecoValido(opcoes.Remetente.Email))
        {
            erros.Add("Email:Remetente:Email deve ser um endereço de e-mail válido.");
        }

        if (opcoes.Modo == OpcoesEmail.ModoPastaLocal)
        {
            if (!desenvolvimento)
            {
                erros.Add("Email:Modo=PastaLocal só é permitido em Development.");
            }

            if (string.IsNullOrWhiteSpace(opcoes.PastaLocal.Caminho) || !Path.IsPathFullyQualified(opcoes.PastaLocal.Caminho))
            {
                erros.Add("Email:PastaLocal:Caminho deve ser um caminho absoluto.");
            }

            return erros;
        }

        var smtp = opcoes.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Servidor) || Uri.CheckHostName(smtp.Servidor.Trim()) == UriHostNameType.Unknown)
        {
            erros.Add("Email:Smtp:Servidor deve ser um nome de servidor ou IP.");
        }

        if (smtp.Porta is < 1 or > 65535)
        {
            erros.Add("Email:Smtp:Porta deve estar entre 1 e 65535.");
        }

        if (smtp.Seguranca is not (OpcoesSmtp.SegurancaStartTls or OpcoesSmtp.SegurancaSslTls or OpcoesSmtp.SegurancaNenhuma))
        {
            erros.Add("Email:Smtp:Seguranca deve ser StartTls, SslTls ou Nenhuma.");
        }
        else if (smtp.Seguranca == OpcoesSmtp.SegurancaNenhuma && !desenvolvimento)
        {
            erros.Add("Email:Smtp:Seguranca=Nenhuma (sem TLS) só é permitido em Development.");
        }

        if (!string.IsNullOrWhiteSpace(smtp.Usuario) && string.IsNullOrWhiteSpace(smtp.SenhaRef))
        {
            erros.Add("Email:Smtp:SenhaRef é obrigatório quando há Email:Smtp:Usuario.");
        }

        if (smtp.TimeoutSegundos is < 5 or > 300)
        {
            erros.Add("Email:Smtp:TimeoutSegundos deve estar entre 5 e 300.");
        }

        if (smtp.TentativasMaximas is < 1 or > 10)
        {
            erros.Add("Email:Smtp:TentativasMaximas deve estar entre 1 e 10.");
        }

        return erros;
    }

    /// <summary>Endereço simples (sem nome, sem lista, sem quebra de linha) — barra injeção de cabeçalho.</summary>
    public static bool EnderecoValido(string? email)
        => !string.IsNullOrWhiteSpace(email)
            && email.IndexOfAny(['\r', '\n', ',', ';', '<', '>']) < 0
            && MailboxAddress.TryParse(email.Trim(), out var endereco)
            && string.Equals(endereco.Address, email.Trim(), StringComparison.Ordinal)
            && endereco.Address.Contains('@', StringComparison.Ordinal);
}
