using System.Globalization;
using MySql.Data.MySqlClient;
using PortalBase.Application.Provisionamento;
using PortalBase.Infrastructure.Multitenancy;

namespace PortalBase.Infrastructure.Provisionamento;

/// <summary>
/// Lock distribuído de provisionamento via <c>GET_LOCK</c> do MySQL (seção 24): o lock vive
/// enquanto a conexão que o obteve permanece aberta, garantindo exclusão mútua entre
/// processos. <c>GET_LOCK(..., 0)</c> não espera: se outro processo já detém, retorna sem lock.
/// </summary>
public sealed class LockProvisionamentoMySql : ILockProvisionamento
{
    private readonly ConstrutorConexaoTenant _construtor;

    public LockProvisionamentoMySql(ConstrutorConexaoTenant construtor)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        _construtor = construtor;
    }

    public async ValueTask<IAsyncDisposable?> TentarAdquirirAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var texto = await _construtor.ConstruirAdminAsync(nomeBase: null, cancellationToken).ConfigureAwait(false);
        var conexao = new MySqlConnection(texto);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);

        var nomeLock = $"portalbase_prov_{tenantId:N}";
        try
        {
            await using var comando = conexao.CreateCommand();
            comando.CommandText = "SELECT GET_LOCK(@nome, 0)";
            comando.Parameters.AddWithValue("@nome", nomeLock);
            var resultado = await comando.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            var adquirido = resultado is not null and not DBNull
                && Convert.ToInt64(resultado, CultureInfo.InvariantCulture) == 1;

            if (!adquirido)
            {
                await conexao.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new TravaMySql(conexao, nomeLock);
        }
        catch
        {
            await conexao.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class TravaMySql : IAsyncDisposable
    {
        private readonly MySqlConnection _conexao;
        private readonly string _nomeLock;

        public TravaMySql(MySqlConnection conexao, string nomeLock)
        {
            _conexao = conexao;
            _nomeLock = nomeLock;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var comando = _conexao.CreateCommand();
                comando.CommandText = "SELECT RELEASE_LOCK(@nome)";
                comando.Parameters.AddWithValue("@nome", _nomeLock);
                await comando.ExecuteScalarAsync().ConfigureAwait(false);
            }
            catch (Exception excecao) when (excecao is not OperationCanceledException)
            {
                // Liberação best-effort: fechar a conexão também libera o lock.
            }
            finally
            {
                await _conexao.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
