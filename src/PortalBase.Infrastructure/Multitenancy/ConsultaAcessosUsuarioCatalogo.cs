using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Multitenancy;

/// <summary>
/// Lê no CATÁLOGO os contribuintes com vínculo ATIVO do usuário (todos, se ele for Administrador da plataforma). Somente-leitura, sem credenciais
/// (regra 7). Parte sempre do <paramref name="usuarioId"/> autenticado — nunca de um tenant informado
/// pelo cliente (regra 4). Ordena por razão social para uma seleção estável.
/// </summary>
public sealed class ConsultaAcessosUsuarioCatalogo : IConsultaAcessosUsuario
{
    private readonly PortalBaseCatalogoDbContext _catalogo;

    public ConsultaAcessosUsuarioCatalogo(PortalBaseCatalogoDbContext catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        _catalogo = catalogo;
    }

    public async Task<IReadOnlyList<AcessoContribuinte>> ListarDoUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (usuarioId == Guid.Empty)
        {
            return [];
        }

        // Administrador da plataforma (usuário ATIVO): pode escolher qualquer contribuinte cadastrado.
        var administradorPlataforma = await _catalogo.Usuarios
            .AsNoTracking()
            .AnyAsync(u => u.Id == usuarioId && u.AdministradorPlataforma && u.Status == StatusUsuario.Ativo, cancellationToken)
            .ConfigureAwait(false);
        var tenants = administradorPlataforma
            ? await _catalogo.Tenants.AsNoTracking().OrderBy(t => t.Nome).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await (
            from vinculo in _catalogo.UsuarioTenants.AsNoTracking()
            where vinculo.UsuarioId == usuarioId && vinculo.Status == StatusAcesso.Ativo
            join tenant in _catalogo.Tenants.AsNoTracking() on vinculo.TenantId equals tenant.Id
            orderby tenant.Nome
            select tenant)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return tenants
            .Select(t => new AcessoContribuinte(t.Id, t.Nome, t.RazaoSocial, t.Cnpj.Valor, t.Estado == EstadoProvisionamento.Ativo))
            .ToList();
    }
}
