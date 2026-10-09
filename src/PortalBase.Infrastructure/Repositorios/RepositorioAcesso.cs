using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Repositorios;

/// <summary>Repositório de gestão de acesso (vínculos usuário×tenant e papéis escopados).</summary>
public sealed class RepositorioAcesso : IRepositorioAcesso
{
    private readonly PortalBaseCatalogoDbContext _contexto;

    public RepositorioAcesso(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<UsuarioTenant?> ObterVinculoAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default)
        => await _contexto.UsuarioTenants
            .FirstOrDefaultAsync(ut => ut.UsuarioId == usuarioId && ut.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);

    public Task AdicionarVinculoAsync(UsuarioTenant vinculo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vinculo);
        _contexto.UsuarioTenants.Add(vinculo);
        return Task.CompletedTask;
    }

    public async Task<bool> PapelDoVinculoExisteAsync(Guid usuarioTenantId, Guid papelId, CancellationToken cancellationToken = default)
        => await _contexto.UsuarioTenantPapeis
            .AnyAsync(utp => utp.UsuarioTenantId == usuarioTenantId && utp.PapelId == papelId, cancellationToken)
            .ConfigureAwait(false);

    public Task AdicionarPapelDoVinculoAsync(UsuarioTenantPapel papel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(papel);
        _contexto.UsuarioTenantPapeis.Add(papel);
        return Task.CompletedTask;
    }

    public async Task<Papel?> ResolverPapelAsync(PapelConhecido papel, CancellationToken cancellationToken = default)
    {
        var nome = papel.ToString();
        // Só papel do SISTEMA (TenantId nulo): um perfil personalizado com o mesmo nome nunca é confundido com ele.
        return await _contexto.Papeis
            .FirstOrDefaultAsync(p => p.Nome == nome && p.TenantId == null, cancellationToken)
            .ConfigureAwait(false);
    }
}
