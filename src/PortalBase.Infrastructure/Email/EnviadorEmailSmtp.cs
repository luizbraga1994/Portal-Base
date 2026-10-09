using MailKit.Security;
using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Notificacoes;
using ClienteSmtp = MailKit.Net.Smtp.SmtpClient;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// Envio pelo servidor SMTP configurado (MailKit). TLS conforme <see cref="OpcoesSmtp.Seguranca"/>: com StartTls o
/// envio FALHA se o servidor não oferecer STARTTLS (nunca cai para texto puro) e o certificado é validado pelo
/// sistema. A senha vem do cofre a cada envio (rotação sem reiniciar). Timeout e cancelamento em todas as etapas.
/// </summary>
public sealed class EnviadorEmailSmtp : IEnviadorEmail
{
    private readonly OpcoesEmail _opcoes;
    private readonly IProvedorSegredos _segredos;
    private readonly IRelogio _relogio;

    public EnviadorEmailSmtp(IOptions<OpcoesEmail> opcoes, IProvedorSegredos segredos, IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentNullException.ThrowIfNull(relogio);
        _opcoes = opcoes.Value;
        _segredos = segredos;
        _relogio = relogio;
    }

    public async Task EnviarAsync(MensagemEmail mensagem, CancellationToken cancellationToken = default)
    {
        var mime = MontadorMensagemEmail.Montar(mensagem, _opcoes, _relogio.AgoraUtc);
        var smtp = _opcoes.Smtp;

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(TimeSpan.FromSeconds(smtp.TimeoutSegundos));

        using var cliente = new ClienteSmtp { Timeout = (int)TimeSpan.FromSeconds(smtp.TimeoutSegundos).TotalMilliseconds };
        await cliente.ConnectAsync(smtp.Servidor.Trim(), smtp.Porta, Seguranca(smtp.Seguranca), limite.Token).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(smtp.Usuario))
            {
                var senha = await _segredos.ObterAsync(smtp.SenhaRef, limite.Token).ConfigureAwait(false);
                await cliente.AuthenticateAsync(smtp.Usuario.Trim(), senha, limite.Token).ConfigureAwait(false);
            }

            await cliente.SendAsync(mime, limite.Token).ConfigureAwait(false);
        }
        finally
        {
            if (cliente.IsConnected)
            {
                await cliente.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static SecureSocketOptions Seguranca(string seguranca) => seguranca switch
    {
        OpcoesSmtp.SegurancaStartTls => SecureSocketOptions.StartTls,
        OpcoesSmtp.SegurancaSslTls => SecureSocketOptions.SslOnConnect,
        OpcoesSmtp.SegurancaNenhuma => SecureSocketOptions.None,
        _ => throw new InvalidOperationException("Email:Smtp:Seguranca inválida."),
    };
}
