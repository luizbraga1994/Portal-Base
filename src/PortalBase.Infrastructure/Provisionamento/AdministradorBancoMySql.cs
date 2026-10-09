using System.Globalization;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using PortalBase.Application.Provisionamento;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Provisionamento;

/// <summary>
/// Operações administrativas de banco (DDL e grants) via credencial ADMINISTRATIVA,
/// separada da de runtime (seção 19). Todo identificador (nome de base, usuário) é
/// validado por <see cref="ConstrutorConexaoTenant"/> antes de compor o DDL — identificadores
/// não podem ser parametrizados, então a validação restrita é a barreira (seções 10, 11, 13).
/// </summary>
public sealed class AdministradorBancoMySql : IAdministradorBancoTenant
{
    /// <summary>
    /// Tabela (de UMA linha) com a marca de proprietário da base: tenant e CNPJ donos dos dados. Criada pelo
    /// provisionamento, fora das migrations do schema do tenant, para que uma base que sobrou (ex.: catálogo perdido)
    /// nunca seja herdada por outro contribuinte com o mesmo nome escolhido.
    /// </summary>
    public const string TabelaProprietario = "portalbase_proprietario";

    private readonly ConstrutorConexaoTenant _construtor;
    private readonly OpcoesBancoTenant _opcoes;

    public AdministradorBancoMySql(ConstrutorConexaoTenant construtor, IOptions<OpcoesBancoTenant> opcoes)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        ArgumentNullException.ThrowIfNull(opcoes);
        _construtor = construtor;
        _opcoes = opcoes.Value;
    }

    public async ValueTask<bool> BancoExisteAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        ConstrutorConexaoTenant.ValidarNomeBase(nomeBase);

        await using var conexao = await AbrirAdminAsync(cancellationToken).ConfigureAwait(false);
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @nome";
        comando.Parameters.AddWithValue("@nome", nomeBase);
        var resultado = await comando.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(resultado, CultureInfo.InvariantCulture) > 0;
    }

    public async ValueTask CriarBancoSeAusenteAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        ConstrutorConexaoTenant.ValidarNomeBase(nomeBase);

        await using var conexao = await AbrirAdminAsync(cancellationToken).ConfigureAwait(false);
        await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // Identificador validado por regex restrito; DDL não aceita parâmetro.
        comando.CommandText =
            $"CREATE DATABASE IF NOT EXISTS `{nomeBase}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs";
#pragma warning restore CA2100
        await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ConcederAcessoRuntimeAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        ConstrutorConexaoTenant.ValidarNomeBase(nomeBase);
        var usuario = ConstrutorConexaoTenant.ValidarUsuario(_opcoes.RuntimeUsuario);

        await using var conexao = await AbrirAdminAsync(cancellationToken).ConfigureAwait(false);
        await using var comando = conexao.CreateCommand();
        // Runtime recebe SOMENTE DML e SOMENTE nesta base (nunca *.*) — seção 18.
#pragma warning disable CA2100 // Identificadores (base, usuário) validados; GRANT não aceita parâmetro.
        comando.CommandText = $"GRANT SELECT, INSERT, UPDATE, DELETE ON `{nomeBase}`.* TO '{usuario}'@'%'";
#pragma warning restore CA2100
        await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoverBancoAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        ConstrutorConexaoTenant.ValidarNomeBase(nomeBase);

        await using var conexao = await AbrirAdminAsync(cancellationToken).ConfigureAwait(false);
        await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // Identificador validado por regex restrito; DDL não aceita parâmetro.
        comando.CommandText = $"DROP DATABASE IF EXISTS `{nomeBase}`";
#pragma warning restore CA2100
        await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProprietarioBaseTenant> ObterProprietarioAsync(string nomeBase, CancellationToken cancellationToken = default)
    {
        ConstrutorConexaoTenant.ValidarNomeBase(nomeBase);

        await using var conexao = await AbrirAdminAsync(cancellationToken).ConfigureAwait(false);
        var tabelas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var comando = conexao.CreateCommand())
        {
            comando.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @nome";
            comando.Parameters.AddWithValue("@nome", nomeBase);
            await using var leitor = await comando.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await leitor.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tabelas.Add(leitor.GetString(0));
            }
        }

        if (tabelas.Count == 0)
        {
            return ProprietarioBaseTenant.BaseVazia;
        }

        if (!tabelas.Contains(TabelaProprietario))
        {
            return new ProprietarioBaseTenant(false, null, null); // tem dados, mas sem dono conhecido
        }

        await using var leitura = conexao.CreateCommand();
#pragma warning disable CA2100 // Identificador validado por regex restrito; nome de base não aceita parâmetro.
        leitura.CommandText = $"SELECT `tenant_id`, `cnpj` FROM `{nomeBase}`.`{TabelaProprietario}` WHERE `id` = 1";
#pragma warning restore CA2100
        await using var linha = await leitura.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        // O conector devolve CHAR(36) como Guid; aceita também texto (outra configuração de conexão).
        Guid? tenantId = !await linha.ReadAsync(cancellationToken).ConfigureAwait(false) ? null : linha.GetValue(0) switch
        {
            Guid id => id,
            string texto when Guid.TryParse(texto, out var id) => id,
            _ => null,
        };

        return tenantId is null || linha.IsDBNull(1)
            ? new ProprietarioBaseTenant(false, null, null) // marca ausente/ilegível conta como sem dono (fail-closed)
            : new ProprietarioBaseTenant(false, tenantId, linha.GetString(1));
    }

    public async ValueTask RegistrarProprietarioAsync(string nomeBase, Guid tenantId, string cnpj, CancellationToken cancellationToken = default)
    {
        ConstrutorConexaoTenant.ValidarNomeBase(nomeBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(cnpj);

        await using var conexao = await AbrirAdminAsync(cancellationToken).ConfigureAwait(false);
        await using (var criar = conexao.CreateCommand())
        {
#pragma warning disable CA2100 // Identificador validado por regex restrito; DDL não aceita parâmetro.
            criar.CommandText =
                $"CREATE TABLE IF NOT EXISTS `{nomeBase}`.`{TabelaProprietario}` (" +
                "`id` TINYINT NOT NULL PRIMARY KEY, `tenant_id` CHAR(36) NOT NULL, `cnpj` VARCHAR(14) NOT NULL, " +
                "`registrado_em_utc` DATETIME(6) NOT NULL, CONSTRAINT `ck_proprietario_linha_unica` CHECK (`id` = 1))";
#pragma warning restore CA2100
            await criar.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var gravar = conexao.CreateCommand();
#pragma warning disable CA2100 // Identificador validado; os VALORES vão por parâmetro.
        gravar.CommandText =
            $"REPLACE INTO `{nomeBase}`.`{TabelaProprietario}` (`id`, `tenant_id`, `cnpj`, `registrado_em_utc`) " +
            "VALUES (1, @tenant, @cnpj, UTC_TIMESTAMP(6))";
#pragma warning restore CA2100
        gravar.Parameters.AddWithValue("@tenant", tenantId.ToString());
        gravar.Parameters.AddWithValue("@cnpj", cnpj);
        await gravar.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MySqlConnection> AbrirAdminAsync(CancellationToken cancellationToken)
    {
        var conexaoTexto = await _construtor.ConstruirAdminAsync(nomeBase: null, cancellationToken).ConfigureAwait(false);
        var conexao = new MySqlConnection(conexaoTexto);
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
}
