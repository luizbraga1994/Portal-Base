using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PortalBase.Application.Provisionamento;
using PortalBase.Infrastructure.Persistencia.BaseTenant;

namespace PortalBase.Infrastructure.Provisionamento;

/// <summary>
/// Última migration do schema dos contribuintes compilada neste assembly (mesma ordem que o EF aplica: pelo id). Lida
/// uma vez dos atributos <see cref="MigrationAttribute"/> do <see cref="PortalBaseTenantDbContext"/> — sem abrir conexão.
/// </summary>
public sealed class VersaoSchemaTenantAssembly : IVersaoSchemaTenant
{
    private static readonly Lazy<string?> Ultima = new(() => typeof(PortalBaseTenantDbContext).Assembly.GetTypes()
        .Where(t => t.GetCustomAttribute<DbContextAttribute>()?.ContextType == typeof(PortalBaseTenantDbContext))
        .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .OrderBy(id => id, StringComparer.Ordinal)
        .LastOrDefault());

    public string? UltimaMigracaoDisponivel => Ultima.Value;
}
