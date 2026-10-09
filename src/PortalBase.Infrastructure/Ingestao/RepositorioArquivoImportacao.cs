using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Arquivos;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Persistencia.BaseTenant;

namespace PortalBase.Infrastructure.Ingestao;

/// <summary>
/// Repositório de METADADOS do arquivo NO BANCO DO TENANT (Prompt 08, seções 17, 18, 19). Obtém
/// o <see cref="PortalBaseTenantDbContext"/> APENAS pela fábrica fail-closed, a partir do
/// <see cref="ITenantContext"/> AUTORIZADO — nunca escolhe a base livremente (regras 4, 5, 6).
/// A idempotência é tenant-scoped (cada base tem seu próprio conjunto de hashes); a violação de
/// unicidade do MySQL (1062) é traduzida em <see cref="ViolacaoUnicidadeException"/>. Descartável
/// (fecha o contexto do tenant ao final do escopo).
/// </summary>
public sealed class RepositorioArquivoImportacao : IRepositorioArquivoImportacao, IDisposable, IAsyncDisposable
{
    private const int MySqlEntradaDuplicada = 1062;

    private readonly IFabricaContextoTenant _fabrica;
    private readonly ITenantContext _tenant;
    private PortalBaseTenantDbContext? _contexto;

    public RepositorioArquivoImportacao(IFabricaContextoTenant fabrica, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(fabrica);
        ArgumentNullException.ThrowIfNull(tenant);
        _fabrica = fabrica;
        _tenant = tenant;
    }

    public async Task<bool> ExisteComHashAsync(string sha256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var contexto = await ObterContextoAsync(cancellationToken).ConfigureAwait(false);
        return await contexto.ArquivosImportacao
            .AsNoTracking()
            .AnyAsync(a => a.Sha256 == sha256, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Guid?> ObterIdPorHashAsync(string sha256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var contexto = await ObterContextoAsync(cancellationToken).ConfigureAwait(false);
        return await contexto.ArquivosImportacao
            .AsNoTracking()
            .Where(a => a.Sha256 == sha256)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ArquivoImportacao?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = await ObterContextoAsync(cancellationToken).ConfigureAwait(false);
        return await contexto.ArquivosImportacao
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AdicionarAsync(ArquivoImportacao arquivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arquivo);
        var contexto = await ObterContextoAsync(cancellationToken).ConfigureAwait(false);
        await contexto.ArquivosImportacao.AddAsync(arquivo, cancellationToken).ConfigureAwait(false);
    }

    public async Task SalvarAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await ObterContextoAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await contexto.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException excecao) when (EhViolacaoUnicidade(excecao))
        {
            throw new ViolacaoUnicidadeException("Arquivo já existente (hash duplicado no tenant).", excecao);
        }
    }

    public async Task<bool> TentarSalvarAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await ObterContextoAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await contexto.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException excecao)
        {
            // Outra operação alterou o arquivo antes (reivindicação perdida): recarrega o estado REAL, não lança.
            foreach (var entrada in excecao.Entries)
            {
                await entrada.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
    }

    public void Dispose()
    {
        _contexto?.Dispose();
        _contexto = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_contexto is not null)
        {
            await _contexto.DisposeAsync().ConfigureAwait(false);
            _contexto = null;
        }
    }

    private async ValueTask<PortalBaseTenantDbContext> ObterContextoAsync(CancellationToken cancellationToken)
        => _contexto ??= await _fabrica.CriarAsync(_tenant, cancellationToken).ConfigureAwait(false);

    private static bool EhViolacaoUnicidade(DbUpdateException excecao)
    {
        for (Exception? atual = excecao.InnerException; atual is not null; atual = atual.InnerException)
        {
            if (atual is MySqlException mysql && mysql.Number == MySqlEntradaDuplicada)
            {
                return true;
            }
        }

        return false;
    }
}
