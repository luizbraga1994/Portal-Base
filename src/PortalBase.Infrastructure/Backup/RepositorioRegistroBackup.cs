using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Backup;
using PortalBase.Domain.Backup;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// Repositório do CATÁLOGO DE BACKUPS (Prompt 50, Fase 13) sobre o banco de CATÁLOGO. Persiste apenas
/// metadados — nunca segredo/chave. A contagem de concluídos ampara a retenção (nunca apagar o único).
/// </summary>
public sealed class RepositorioRegistroBackup : IRepositorioRegistroBackup
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public RepositorioRegistroBackup(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task AdicionarAsync(RegistroBackup registro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registro);
        _contexto.RegistrosBackup.Add(registro);
        await _contexto.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AtualizarAsync(RegistroBackup registro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registro);
        _contexto.RegistrosBackup.Update(registro);
        await _contexto.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RegistroBackup?> ObterAsync(Guid backupId, CancellationToken cancellationToken = default)
        => await _contexto.RegistrosBackup
            .FirstOrDefaultAsync(b => b.Id == backupId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<RegistroBackup>> ListarPorTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => await _contexto.RegistrosBackup
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .OrderByDescending(b => b.IniciadoEmUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<RegistroBackup>> ListarConcluidosAsync(CancellationToken cancellationToken = default)
        => await _contexto.RegistrosBackup
            .AsNoTracking()
            .Where(b => b.Status == StatusBackup.Concluido)
            .OrderBy(b => b.RetencaoAteUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<int> ContarConcluidosAsync(Guid? tenantId, TipoBackup tipo, CancellationToken cancellationToken = default)
        => await _contexto.RegistrosBackup
            .AsNoTracking()
            .Where(b => b.Tipo == tipo && b.Status == StatusBackup.Concluido && b.TenantId == tenantId)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> ExisteConcluidoOuEmAndamentoDesdeAsync(
        TipoBackup tipo, DateTimeOffset desdeUtc, DateTimeOffset emAndamentoDesdeUtc, CancellationToken cancellationToken = default)
        => await _contexto.RegistrosBackup
            .AsNoTracking()
            .AnyAsync(
                b => b.Tipo == tipo && b.IniciadoEmUtc >= desdeUtc
                    && (b.Status == StatusBackup.Concluido || (b.Status == StatusBackup.Iniciado && b.IniciadoEmUtc >= emAndamentoDesdeUtc)),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<RegistroBackup>> ListarIniciadosAntesDeAsync(DateTimeOffset antesDeUtc, CancellationToken cancellationToken = default)
        => await _contexto.RegistrosBackup
            .Where(b => b.Status == StatusBackup.Iniciado && b.IniciadoEmUtc < antesDeUtc)
            .OrderBy(b => b.IniciadoEmUtc)
            .Take(500)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<RegistroBackup>> ListarConcluidosVencidosAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
        => await _contexto.RegistrosBackup
            .Where(b => b.Status == StatusBackup.Concluido && b.RetencaoAteUtc != null && b.RetencaoAteUtc <= agoraUtc)
            .OrderBy(b => b.RetencaoAteUtc)
            .Take(1000)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<RegistroBackup>> ListarRecentesAsync(int quantidade, CancellationToken cancellationToken = default)
        => await _contexto.RegistrosBackup
            .AsNoTracking()
            .OrderByDescending(b => b.IniciadoEmUtc)
            .Take(Math.Clamp(quantidade, 1, 200))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
