using PortalBase.Application.Abstracoes;

namespace PortalBase.Infrastructure.Tempo;

/// <summary>Relógio do sistema baseado em <see cref="TimeProvider"/>.</summary>
public sealed class RelogioSistema : IRelogio
{
    public DateTimeOffset AgoraUtc => TimeProvider.System.GetUtcNow();
}
