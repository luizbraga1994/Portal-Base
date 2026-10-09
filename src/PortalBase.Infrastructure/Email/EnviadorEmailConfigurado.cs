using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Notificacoes;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// Transporte conforme a configuração EM VIGOR no momento do envio (a da tela ou a do servidor): SMTP, pasta local
/// (só Development) ou desligado. Assim, mudar o e-mail pela tela vale no próximo envio, sem reiniciar.
/// </summary>
public sealed class EnviadorEmailConfigurado : IEnviadorEmail
{
    private readonly IOptions<OpcoesEmail> _opcoes;
    private readonly IProvedorSegredos _segredos;
    private readonly IRelogio _relogio;

    public EnviadorEmailConfigurado(IOptions<OpcoesEmail> opcoes, IProvedorSegredos segredos, IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentNullException.ThrowIfNull(relogio);
        _opcoes = opcoes;
        _segredos = segredos;
        _relogio = relogio;
    }

    public Task EnviarAsync(MensagemEmail mensagem, CancellationToken cancellationToken = default)
    {
        // Um retrato da configuração para o envio inteiro (não muda no meio da conexão).
        var retrato = Options.Create(_opcoes.Value);
        return retrato.Value.Modo switch
        {
            OpcoesEmail.ModoSmtp => new EnviadorEmailSmtp(retrato, _segredos, _relogio).EnviarAsync(mensagem, cancellationToken),
            OpcoesEmail.ModoPastaLocal => new EnviadorEmailPastaLocal(retrato, _relogio).EnviarAsync(mensagem, cancellationToken),
            _ => throw new InvalidOperationException("O envio de e-mail está desligado."),
        };
    }
}
