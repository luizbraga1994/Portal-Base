using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Tarefas;
using PortalBase.Domain.Tarefas;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Tarefas;

/// <summary>Tarefas em segundo plano no CATÁLOGO (ARQ-006). Concorrência otimista pelo <c>ConcurrencyStamp</c>.</summary>
public sealed class RepositorioTarefasCatalogo : IRepositorioTarefas
{
    private readonly PortalBaseCatalogoDbContext _catalogo;

    public RepositorioTarefasCatalogo(PortalBaseCatalogoDbContext catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        _catalogo = catalogo;
    }

    public async Task AdicionarAsync(TarefaSegundoPlano tarefa, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tarefa);
        await _catalogo.TarefasSegundoPlano.AddAsync(tarefa, cancellationToken).ConfigureAwait(false);
    }

    public Task<TarefaSegundoPlano?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _catalogo.TarefasSegundoPlano.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<TarefaSegundoPlano?> LerAsync(Guid id, CancellationToken cancellationToken = default)
        => _catalogo.TarefasSegundoPlano.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<TarefaSegundoPlano?> ObterAtivaAsync(Guid tenantId, TipoTarefaSegundoPlano tipo, string chaveIdempotencia, CancellationToken cancellationToken = default)
        => _catalogo.TarefasSegundoPlano
            .Where(x => x.TenantId == tenantId && x.Tipo == tipo && x.ChaveIdempotencia == chaveIdempotencia
                && (x.Estado == EstadoTarefaSegundoPlano.Pendente || x.Estado == EstadoTarefaSegundoPlano.EmExecucao))
            .OrderByDescending(x => x.CriadaEmUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<TarefaSegundoPlano>> ListarReivindicaveisAsync(DateTimeOffset agoraUtc, int limite, CancellationToken cancellationToken = default)
        => await _catalogo.TarefasSegundoPlano
            .Where(x => x.Estado == EstadoTarefaSegundoPlano.Pendente
                || (x.Estado == EstadoTarefaSegundoPlano.EmExecucao && x.LeaseAteUtc != null && x.LeaseAteUtc <= agoraUtc))
            .OrderBy(x => x.CriadaEmUtc)
            .Take(Math.Clamp(limite, 1, 50))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task SalvarAsync(CancellationToken cancellationToken = default) => _catalogo.SaveChangesAsync(cancellationToken);

    public async Task<bool> TentarSalvarAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _catalogo.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException excecao)
        {
            foreach (var entrada in excecao.Entries)
            {
                await entrada.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
    }
}
