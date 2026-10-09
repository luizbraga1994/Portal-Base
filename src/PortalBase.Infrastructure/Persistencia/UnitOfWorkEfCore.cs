using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Excecoes;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Persistencia;

/// <summary>
/// Unidade de trabalho sobre o banco de CATÁLOGO. Traduz violações de restrição de
/// unicidade do MySQL (erro 1062) em <see cref="ViolacaoUnicidadeException"/>, para que
/// os casos de uso tratem duplicidade sob concorrência como conflito (seção 25).
/// </summary>
public sealed class UnitOfWorkEfCore : IUnitOfWork
{
    private const int MySqlEntradaDuplicada = 1062;

    private readonly PortalBaseCatalogoDbContext _contexto;

    public UnitOfWorkEfCore(PortalBaseCatalogoDbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        _contexto = contexto;
    }

    public async Task<int> SalvarAlteracoesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _contexto.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException excecao) when (EhViolacaoUnicidade(excecao))
        {
            throw new ViolacaoUnicidadeException("Violação de restrição de unicidade.", excecao);
        }
    }

    public async Task ExecutarEmTransacaoAsync(
        Func<CancellationToken, Task> operacao,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operacao);

        var estrategia = _contexto.Database.CreateExecutionStrategy();

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transacao = await _contexto.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await operacao(cancellationToken).ConfigureAwait(false);

            await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

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
