using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Backup;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// Lista os tenants candidatos a backup a partir do CATÁLOGO (Prompt 50, Fases 5/7). Só tenants
/// PROVISIONADOS (Pronto/Ativo) têm base física válida ⇒ são backup-ready; tenants em Pendente/
/// Provisionando/Falhado são IGNORADOS (uma base parcial não gera backup válido). NUNCA expõe credencial.
/// </summary>
public sealed class ProvedorAlvosBackupCatalogo : IProvedorAlvosBackup
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public ProvedorAlvosBackupCatalogo(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<IReadOnlyList<AlvoBackupTenant>> ListarBackupReadyAsync(CancellationToken cancellationToken = default)
        => await _contexto.Tenants
            .AsNoTracking()
            .Where(t => t.Estado == EstadoProvisionamento.Pronto || t.Estado == EstadoProvisionamento.Ativo)
            .Select(t => new AlvoBackupTenant(t.Id, t.NomeBaseFisica, t.Estado))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<AlvoBackupTenant?> ObterAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => await _contexto.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new AlvoBackupTenant(t.Id, t.NomeBaseFisica, t.Estado))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>Lido da conexão do catálogo já configurada no servidor (sem abrir conexão, sem credencial).</remarks>
    public string? NomeBaseCatalogo
    {
        get
        {
            var nome = _contexto.Database.GetDbConnection().Database;
            return string.IsNullOrWhiteSpace(nome) ? null : nome;
        }
    }
}
