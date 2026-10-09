using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Notificacoes;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// SÓ Development: em vez de enviar, grava cada e-mail como arquivo .eml numa pasta privada (abre no Outlook/Thunderbird
/// ou em qualquer editor). Permite testar a recuperação de senha sem servidor SMTP. O validador da configuração
/// recusa este modo fora de Development.
/// </summary>
public sealed class EnviadorEmailPastaLocal : IEnviadorEmail
{
    private readonly OpcoesEmail _opcoes;
    private readonly IRelogio _relogio;

    public EnviadorEmailPastaLocal(IOptions<OpcoesEmail> opcoes, IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(relogio);
        _opcoes = opcoes.Value;
        _relogio = relogio;
    }

    public async Task EnviarAsync(MensagemEmail mensagem, CancellationToken cancellationToken = default)
    {
        var agora = _relogio.AgoraUtc;
        var mime = MontadorMensagemEmail.Montar(mensagem, _opcoes, agora);
        var pasta = _opcoes.PastaLocal.Caminho;

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(pasta); // herda a ACL restrita da pasta de dados criada pelo script de ambiente
        }
        else
        {
            Directory.CreateDirectory(pasta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Grava com outro nome e só no fim renomeia para .eml: quem lê a pasta (o leitor de e-mail ou um teste) nunca
        // encontra o arquivo pela metade nem ainda aberto pela gravação.
        var nome = $"{agora.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var parcial = Path.Combine(pasta, nome + ".gravando");
        try
        {
            await mime.WriteToAsync(parcial, cancellationToken).ConfigureAwait(false);
            File.Move(parcial, Path.Combine(pasta, nome + ".eml"));
        }
        catch
        {
            File.Delete(parcial);
            throw;
        }
    }
}
