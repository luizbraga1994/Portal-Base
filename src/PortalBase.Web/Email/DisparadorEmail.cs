using PortalBase.Infrastructure.Email;

namespace PortalBase.Web.Email;

/// <summary>
/// Hospeda o <see cref="ProcessadorFilaEmail"/>: envia em segundo plano o que os casos de uso enfileiram. Lê a
/// configuração de e-mail da tela ao subir e a relê a cada minuto (outra instância pode tê-la mudado).
/// </summary>
public sealed class DisparadorEmail : BackgroundService
{
    private static readonly TimeSpan IntervaloRecarga = TimeSpan.FromMinutes(1);

    private readonly ProcessadorFilaEmail _processador;
    private readonly ProvedorOpcoesEmail _opcoes;

    public DisparadorEmail(ProcessadorFilaEmail processador, ProvedorOpcoesEmail opcoes)
    {
        ArgumentNullException.ThrowIfNull(processador);
        ArgumentNullException.ThrowIfNull(opcoes);
        _processador = processador;
        _opcoes = opcoes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _opcoes.RecarregarAsync(stoppingToken).ConfigureAwait(false);
        await Task.WhenAll(_processador.ProcessarAsync(stoppingToken), RecarregarPeriodicamenteAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task RecarregarPeriodicamenteAsync(CancellationToken stoppingToken)
    {
        using var relogio = new PeriodicTimer(IntervaloRecarga);
        try
        {
            while (await relogio.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await _opcoes.RecarregarAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // desligamento do host
        }
    }
}
