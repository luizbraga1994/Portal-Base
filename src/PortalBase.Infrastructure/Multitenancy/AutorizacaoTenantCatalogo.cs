using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Multitenancy;

/// <summary>
/// Autorização de tenant contra o CATÁLOGO (deny-by-default, seções 5, 26, 33). Acesso FUNCIONAL
/// (<see cref="PodeAcessarAsync"/>) só é autorizado se: o principal é um usuário ATIVO, possui
/// VÍNCULO ativo com o tenant (ou é Administrador da plataforma) e o tenant está OPERACIONAL
/// (<see cref="EstadoProvisionamento.Ativo"/>).
/// Acesso ADMINISTRATIVO (<see cref="PodeAdministrarAsync"/> — ciclo de vida) exige usuário ativo +
/// vínculo ativo, mas NÃO exige tenant operacional. Qualquer condição ausente ⇒ negado.
/// </summary>
public sealed class AutorizacaoTenantCatalogo : IAutorizacaoTenant
{
    private readonly PortalBaseCatalogoDbContext _catalogo;

    public AutorizacaoTenantCatalogo(PortalBaseCatalogoDbContext catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        _catalogo = catalogo;
    }

    public async ValueTask<bool> PodeAcessarAsync(string principalId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!ValidarIdentificadores(principalId, tenantId, out var usuarioId))
        {
            return false;
        }

        // Acesso funcional exige tenant OPERACIONAL (Ativo — Prompt 09, seção 4).
        var tenantOperacional = await _catalogo.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.Estado == EstadoProvisionamento.Ativo, cancellationToken)
            .ConfigureAwait(false);
        if (!tenantOperacional)
        {
            return false;
        }

        return await UsuarioComVinculoAtivoAsync(usuarioId, tenantId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> PodeAdministrarAsync(string principalId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!ValidarIdentificadores(principalId, tenantId, out var usuarioId))
        {
            return false;
        }

        // Administração de ciclo de vida NÃO exige tenant operacional, mas o tenant deve existir.
        var tenantExiste = await _catalogo.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!tenantExiste)
        {
            return false;
        }

        return await UsuarioComVinculoAtivoAsync(usuarioId, tenantId, cancellationToken).ConfigureAwait(false);
    }

    private static bool ValidarIdentificadores(string principalId, Guid tenantId, out Guid usuarioId)
    {
        // O principal identifica o usuário do catálogo (Guid). Entrada inválida ⇒ negado.
        return Guid.TryParse(principalId, out usuarioId) && usuarioId != Guid.Empty && tenantId != Guid.Empty;
    }

    private async ValueTask<bool> UsuarioComVinculoAtivoAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken)
    {
        var usuario = await _catalogo.Usuarios
            .AsNoTracking()
            .Where(u => u.Id == usuarioId && u.Status == StatusUsuario.Ativo)
            .Select(u => new { u.AdministradorPlataforma })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (usuario is null)
        {
            return false;
        }

        // Administrador da plataforma (perfil global, lido do catálogo): acesso a QUALQUER contribuinte, sem vínculo.
        if (usuario.AdministradorPlataforma)
        {
            return true;
        }

        return await _catalogo.UsuarioTenants
            .AsNoTracking()
            .AnyAsync(
                v => v.UsuarioId == usuarioId && v.TenantId == tenantId && v.Status == StatusAcesso.Ativo,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
