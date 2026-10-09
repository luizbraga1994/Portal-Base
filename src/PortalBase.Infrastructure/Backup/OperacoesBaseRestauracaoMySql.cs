using System.Globalization;
using System.Text.RegularExpressions;
using MySql.Data.MySqlClient;
using PortalBase.Application.Backup;
using PortalBase.Infrastructure.Multitenancy;

namespace PortalBase.Infrastructure.Backup;

/// <summary>
/// Operações de base da restauração aplicada, com a credencial ADMINISTRATIVA do MySQL:
/// <list type="bullet">
/// <item><see cref="BloquearAsync"/> — <c>GET_LOCK</c> numa conexão mantida aberta até o descarte: uma operação por
/// alvo, valendo entre instâncias da Web. Ocupado ⇒ <see cref="OperacaoBackupEmAndamentoException"/> na hora.</item>
/// <item><see cref="TrocarTabelasAsync"/> — cria a base PRESERVADA e executa UM <c>RENAME TABLE</c> com todos os pares
/// (produção → preservada, restaurada → produção): atômico no MySQL (ou tudo, ou nada); as chaves estrangeiras seguem as
/// suas tabelas e os grants da produção (em nível de base) valem para as tabelas que entram. A base preservada não
/// recebe grant de runtime (fica fora do alcance do portal). Bases com views/objetos que não são tabela são recusadas.</item>
/// </list>
/// Identificadores validados (nome de base pelo <see cref="ConstrutorConexaoTenant"/>; nome de tabela por regex
/// restrito) — DDL não aceita parâmetro.
/// </summary>
public sealed partial class OperacoesBaseRestauracaoMySql : IOperacoesBaseRestauracao
{
    private readonly ConstrutorConexaoTenant _construtor;

    public OperacoesBaseRestauracaoMySql(ConstrutorConexaoTenant construtor)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        _construtor = construtor;
    }

    public async Task<IAsyncDisposable> BloquearAsync(string chave, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chave);
        if (!PadraoChave().IsMatch(chave))
        {
            throw new ArgumentException("Chave de bloqueio inválida.", nameof(chave));
        }

        var conexao = await AbrirAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var comando = conexao.CreateCommand();
            comando.CommandText = "SELECT GET_LOCK(@chave, 0)";
            comando.Parameters.AddWithValue("@chave", chave);
            var obtido = await comando.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (obtido is null or DBNull || Convert.ToInt64(obtido, CultureInfo.InvariantCulture) != 1)
            {
                throw new OperacaoBackupEmAndamentoException();
            }

            return new Bloqueio(conexao, chave);
        }
        catch
        {
            await conexao.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task TrocarTabelasAsync(string baseProducao, string baseRestaurada, string basePreservada, CancellationToken cancellationToken = default)
    {
        var producao = ConstrutorConexaoTenant.ValidarNomeBase(baseProducao);
        var restaurada = ConstrutorConexaoTenant.ValidarNomeBase(baseRestaurada);
        var preservada = ConstrutorConexaoTenant.ValidarNomeBase(basePreservada);
        if (new[] { producao, restaurada, preservada }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
        {
            throw new ArgumentException("As três bases da troca precisam ser distintas.");
        }

        await using var conexao = await AbrirAsync(cancellationToken).ConfigureAwait(false);
        var tabelasProducao = await ListarTabelasAsync(conexao, producao, cancellationToken).ConfigureAwait(false);
        var tabelasRestauradas = await ListarTabelasAsync(conexao, restaurada, cancellationToken).ConfigureAwait(false);
        if (tabelasRestauradas.Count == 0)
        {
            throw new InvalidOperationException("A base restaurada não tem tabelas.");
        }

        await ExecutarAsync(conexao, $"CREATE DATABASE `{preservada}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs", cancellationToken).ConfigureAwait(false);

        var pares = tabelasProducao.Select(t => $"`{producao}`.`{t}` TO `{preservada}`.`{t}`")
            .Concat(tabelasRestauradas.Select(t => $"`{restaurada}`.`{t}` TO `{producao}`.`{t}`"));
        try
        {
            await ExecutarAsync(conexao, "RENAME TABLE " + string.Join(", ", pares), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // RENAME TABLE é atômico: falhou ⇒ nada saiu do lugar e a preservada está vazia — pode ser removida.
            if ((await ListarTabelasAsync(conexao, preservada, CancellationToken.None).ConfigureAwait(false)).Count == 0)
            {
                await ExecutarAsync(conexao, $"DROP DATABASE IF EXISTS `{preservada}`", CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        // A base de preparo ficou vazia: removida (a de produção e a preservada ficam).
        if ((await ListarTabelasAsync(conexao, restaurada, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            await ExecutarAsync(conexao, $"DROP DATABASE IF EXISTS `{restaurada}`", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<string>> ListarTabelasAsync(MySqlConnection conexao, string nomeBase, CancellationToken cancellationToken)
    {
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = @base";
        comando.Parameters.AddWithValue("@base", nomeBase);
        var tabelas = new List<string>();
        await using var leitor = await comando.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await leitor.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var nome = leitor.GetString(0);
            if (!string.Equals(leitor.GetString(1), "BASE TABLE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A base tem objetos que não são tabelas (views); a troca não é suportada.");
            }

            if (!PadraoTabela().IsMatch(nome))
            {
                throw new InvalidOperationException("Nome de tabela fora do padrão permitido; a troca foi recusada.");
            }

            tabelas.Add(nome);
        }

        return tabelas;
    }

    private static async Task ExecutarAsync(MySqlConnection conexao, string sql, CancellationToken cancellationToken)
    {
        await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // Identificadores validados (base pelo ConstrutorConexaoTenant, tabela por regex); DDL não aceita parâmetro.
        comando.CommandText = sql;
#pragma warning restore CA2100
        comando.CommandTimeout = 600;
        await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MySqlConnection> AbrirAsync(CancellationToken cancellationToken)
    {
        var texto = await _construtor.ConstruirAdminAsync(nomeBase: null, cancellationToken).ConfigureAwait(false);
        var conexao = new MySqlConnection(texto);
        try
        {
            await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
            return conexao;
        }
        catch
        {
            await conexao.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$")]
    private static partial Regex PadraoChave();

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex PadraoTabela();

    /// <summary>Libera o <c>GET_LOCK</c> e fecha a conexão que o segura.</summary>
    private sealed class Bloqueio(MySqlConnection conexao, string chave) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var comando = conexao.CreateCommand();
                comando.CommandText = "SELECT RELEASE_LOCK(@chave)";
                comando.Parameters.AddWithValue("@chave", chave);
                await comando.ExecuteScalarAsync().ConfigureAwait(false);
            }
            finally
            {
                await conexao.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
