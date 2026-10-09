using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;
using PortalBase.Application.Segredos;

namespace PortalBase.Integration.Tests.Infra;

/// <summary>Fakes locais para os testes UNITÁRIOS de segredos (sem MySQL).</summary>
internal sealed class RelogioFixo(DateTimeOffset agora) : IRelogio
{
    public DateTimeOffset AgoraUtc { get; } = agora;
}

internal sealed class ContextoSegurancaFake : IContextoSeguranca
{
    public bool EstaAutenticado { get; set; } = true;

    public string? PrincipalId { get; set; } = "user-1";

    public bool MfaSatisfeito { get; set; }
}

internal sealed class AuditoriaColetora : IAuditoria
{
    public List<EventoAuditoria> Eventos { get; } = [];

    public ValueTask RegistrarAsync(EventoAuditoria evento, CancellationToken cancellationToken = default)
    {
        Eventos.Add(evento);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ProvedorSegredosFake : IProvedorSegredos, IEscritorSegredos
{
    public Dictionary<string, string> Valores { get; } = new(StringComparer.Ordinal);

    public int Chamadas { get; private set; }

    public int Escritas { get; private set; }

    public ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        return Valores.TryGetValue(referencia, out var valor)
            ? ValueTask.FromResult(valor)
            : throw new InvalidOperationException($"Segredo '{referencia}' ausente.");
    }

    public ValueTask DefinirAsync(string referencia, string valor, CancellationToken cancellationToken = default)
    {
        Escritas++;
        Valores[referencia] = valor;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ServicoAutorizacaoFake : IServicoAutorizacao
{
    public HashSet<(Guid Tenant, string Permissao)> Permitidos { get; } = [];

    public ValueTask<ResultadoAutorizacao> AvaliarAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Permitidos.Contains((tenantId, permissao))
            ? ResultadoAutorizacao.Ok
            : ResultadoAutorizacao.Negar(MotivoNegacao.SemPermissao));

    public async ValueTask ExigirAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
    {
        if (!Permitidos.Contains((tenantId, permissao)))
        {
            throw new PortalBase.Application.Excecoes.AutorizacaoNegadaException();
        }

        await ValueTask.CompletedTask;
    }

    public ValueTask<ResultadoAutorizacao> AvaliarAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
        => AvaliarAsync(tenantId, permissao, cancellationToken);

    public ValueTask ExigirAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
        => ExigirAsync(tenantId, permissao, cancellationToken);
}

