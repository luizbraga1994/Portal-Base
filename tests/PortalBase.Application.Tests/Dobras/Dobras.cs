using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;

namespace PortalBase.Application.Tests.Dobras;

/// <summary>Sink de auditoria em memória para verificação nos testes.</summary>
internal sealed class AuditoriaFake : IAuditoria
{
    public List<EventoAuditoria> Eventos { get; } = [];

    public ValueTask RegistrarAsync(EventoAuditoria evento, CancellationToken cancellationToken = default)
    {
        Eventos.Add(evento);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Relógio fixo para tornar o tempo determinístico nos testes.</summary>
internal sealed class RelogioFixo : IRelogio
{
    public RelogioFixo(DateTimeOffset agora) => AgoraUtc = agora;

    public DateTimeOffset AgoraUtc { get; }
}
