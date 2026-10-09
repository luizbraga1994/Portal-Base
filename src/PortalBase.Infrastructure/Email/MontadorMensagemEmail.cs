using MimeKit;
using PortalBase.Application.Notificacoes;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// Monta a mensagem MIME (cabeçalhos codificados pelo MimeKit; destinatário validado — sem injeção de cabeçalho), com o
/// layout e o tema configurados (<see cref="LayoutEmail"/>) e o link do portal no rodapé.
/// </summary>
internal static class MontadorMensagemEmail
{
    public static MimeMessage Montar(MensagemEmail mensagem, OpcoesEmail opcoes, DateTimeOffset agoraUtc)
    {
        ArgumentNullException.ThrowIfNull(mensagem);
        ArgumentNullException.ThrowIfNull(opcoes);
        var remetente = opcoes.Remetente;

        if (!ValidadorOpcoesEmail.EnderecoValido(mensagem.Para))
        {
            throw new ArgumentException("Destinatário inválido.", nameof(mensagem));
        }

        if (string.IsNullOrWhiteSpace(mensagem.Assunto) || mensagem.Assunto.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("Assunto inválido.", nameof(mensagem));
        }

        var mime = new MimeMessage
        {
            Subject = mensagem.Assunto,
            Date = agoraUtc,
        };
        mime.From.Add(new MailboxAddress(remetente.Nome, remetente.Email.Trim()));
        mime.To.Add(MailboxAddress.Parse(mensagem.Para.Trim()));

        var (texto, html) = LayoutEmail.Aplicar(mensagem, opcoes);
        var corpo = new BodyBuilder { TextBody = texto, HtmlBody = html };

        mime.Body = corpo.ToMessageBody();
        return mime;
    }
}
