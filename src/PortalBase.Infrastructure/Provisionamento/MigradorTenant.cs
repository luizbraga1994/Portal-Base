using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Provisionamento;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Persistencia.BaseTenant;

namespace PortalBase.Infrastructure.Provisionamento;

/// <summary>
/// Aplica as migrations do schema do tenant a uma base específica (credencial ADMIN, que tem
/// DDL) e valida a conectividade com a credencial de RUNTIME (a que a aplicação usa).
/// A versão de schema é o id da última migration aplicada (seção 16).
/// </summary>
public sealed class MigradorTenant : IMigradorTenant
{
    private readonly ConstrutorConexaoTenant _construtor;

    public MigradorTenant(ConstrutorConexaoTenant construtor)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        _construtor = construtor;
    }

    public async ValueTask<ResultadoMigracao> AplicarMigrationsAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        ConstrutorConexaoTenant.ValidarNomeBase(nomeBase);

        var conexao = await _construtor.ConstruirAdminAsync(nomeBase, cancellationToken).ConfigureAwait(false);
        await using var contexto = CriarContexto(conexao);

        await contexto.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var aplicadas = await contexto.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var ultima = aplicadas.LastOrDefault() ?? "(nenhuma)";
        return new ResultadoMigracao(ultima, ultima);
    }

    public async ValueTask<bool> PodeConectarAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        ConstrutorConexaoTenant.ValidarNomeBase(nomeBase);

        var conexao = await _construtor.ConstruirRuntimeAsync(nomeBase, cancellationToken).ConfigureAwait(false);
        await using var contexto = CriarContexto(conexao);

        try
        {
            return await contexto.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            return false;
        }
    }

    private static PortalBaseTenantDbContext CriarContexto(string conexao)
    {
        var opcoes = new DbContextOptionsBuilder<PortalBaseTenantDbContext>()
            .UseMySQL(conexao)
            .Options;
        return new PortalBaseTenantDbContext(opcoes);
    }
}
