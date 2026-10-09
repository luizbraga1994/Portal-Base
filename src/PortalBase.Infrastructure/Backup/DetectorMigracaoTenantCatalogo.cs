using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Backup;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// Detecta provisionamento/migration em andamento de um tenant (Prompt 50, Fase 8) a partir do estado
/// do catálogo: um tenant em <see cref="EstadoProvisionamento.Provisionando"/> está com a base sob
/// criação/migração ⇒ não é seguro fazer backup (evita artefato inconsistente). Para o CATÁLOGO
/// (tenantId nulo) este detector é conservador e retorna <c>false</c> — o rastreio de migration do
/// próprio catálogo é uma dependência externa (documentada em BACKUP-RESTORE-DR.md).
/// </summary>
public sealed class DetectorMigracaoTenantCatalogo : IDetectorMigracaoEmAndamento
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public DetectorMigracaoTenantCatalogo(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<bool> MigracaoEmAndamentoAsync(Guid? tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId is null)
        {
            return false;
        }

        return await _contexto.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId.Value && t.Estado == EstadoProvisionamento.Provisionando, cancellationToken)
            .ConfigureAwait(false);
    }
}
