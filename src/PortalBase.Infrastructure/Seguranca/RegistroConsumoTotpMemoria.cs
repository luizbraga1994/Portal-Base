using PortalBase.Application.Autorizacao;

namespace PortalBase.Infrastructure.Seguranca;

/// <summary>
/// ANTI-REPLAY de TOTP em memória (Prompt 68) — singleton por processo, thread-safe. Guarda, por usuário, o
/// MAIOR passo (contador de 30s) já consumido no step-up e rejeita a reapresentação do mesmo código (ou de um
/// anterior) dentro da sua janela de validade (~30-90s). NUNCA guarda segredo/código — apenas o número do passo
/// e o instante (para poda). É defesa DENTRO do processo; uma topologia multi-instância exige um armazenamento
/// distribuído (registrado como item aberto/DEFERRED), mas isto já fecha o vetor no caso de instância única.
/// </summary>
public sealed class RegistroConsumoTotpMemoria : IRegistroConsumoTotp
{
    // Retenção bem acima da validade de um código (~90s): suficiente para bloquear replay sem crescer sem limite.
    private static readonly TimeSpan Retencao = TimeSpan.FromMinutes(10);

    private readonly object _trava = new();
    private readonly Dictionary<Guid, (long Passo, DateTimeOffset QuandoUtc)> _ultimoPasso = new();

    public bool TentarConsumir(Guid usuarioId, long passo, DateTimeOffset agoraUtc)
    {
        lock (_trava)
        {
            PodarExpirados(agoraUtc);

            if (_ultimoPasso.TryGetValue(usuarioId, out var registro) && passo <= registro.Passo)
            {
                return false; // replay: passo já consumido (ou anterior) — nega, sem atualizar.
            }

            _ultimoPasso[usuarioId] = (passo, agoraUtc);
            return true;
        }
    }

    // Remove entradas antigas (fora da janela de retenção) para limitar o uso de memória. O(n) sob lock, mas n é
    // pequeno (usuários com step-up recente) e a operação é rara. Nunca expõe nada — só metadado técnico.
    private void PodarExpirados(DateTimeOffset agoraUtc)
    {
        if (_ultimoPasso.Count == 0)
        {
            return;
        }

        var limite = agoraUtc - Retencao;
        List<Guid>? expirados = null;
        foreach (var (usuarioId, registro) in _ultimoPasso)
        {
            if (registro.QuandoUtc < limite)
            {
                (expirados ??= []).Add(usuarioId);
            }
        }

        if (expirados is not null)
        {
            foreach (var usuarioId in expirados)
            {
                _ultimoPasso.Remove(usuarioId);
            }
        }
    }
}
