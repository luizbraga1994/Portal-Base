using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using PortalBase.Application.DependencyInjection;
using PortalBase.Infrastructure.DependencyInjection;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Integration.Tests.Infra;

/// <summary>
/// Sobe um ambiente multi-tenant REAL sobre MySQL para os testes de isolamento:
/// cria um catálogo dedicado e um usuário de RUNTIME sem privilégios (o provisionamento
/// concede DML por base), e monta a DI de produção (AddApplication + AddInfrastructure).
///
/// Requer a variável de ambiente <c>PORTALBASE_TEST_MYSQL_ADMIN</c> com uma conexão
/// administrativa (CREATE DATABASE/USER/GRANT). Sem ela, <see cref="Disponivel"/> é falso e os
/// testes são pulados.
/// </summary>
public sealed class FixtureMultiTenant : IAsyncLifetime
{
    private readonly List<Guid> _tenantsCriados = [];
    private string _catalogoDb = string.Empty;
    private string _appUser = string.Empty;
    private ServiceProvider? _provedor;

    public bool Disponivel { get; private set; }

    /// <summary>Nome do banco de catálogo dedicado deste run (para testes de grants).</summary>
    public string CatalogoDb => _catalogoDb;

    public IServiceProvider Servicos => _provedor ?? throw new InvalidOperationException("Fixture indisponível.");

    public IServiceScope CriarEscopo() => Servicos.CreateScope();

    public void RegistrarTenant(Guid id) => _tenantsCriados.Add(id);

    public async Task InitializeAsync()
    {
        var conexaoAdmin = Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN");
        if (string.IsNullOrWhiteSpace(conexaoAdmin))
        {
            Disponivel = false;
            return;
        }

        var sufixo = Guid.NewGuid().ToString("N")[..8];
        _catalogoDb = $"pb_it_cat_{sufixo}";
        _appUser = $"pb_it_app_{sufixo}";
        var appSenha = $"Ap1_{sufixo}";

        var admin = new MySqlConnectionStringBuilder(conexaoAdmin);

        await using (var conexao = new MySqlConnection(conexaoAdmin))
        {
            await conexao.OpenAsync();
            await ExecutarAsync(conexao, $"CREATE DATABASE `{_catalogoDb}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs");
            await ExecutarAsync(conexao, $"CREATE USER '{_appUser}'@'%' IDENTIFIED BY '{appSenha}'");
        }

        var conexaoCatalogo = new MySqlConnectionStringBuilder(conexaoAdmin) { Database = _catalogoDb }.ConnectionString;
        var porta = admin.Port.ToString(CultureInfo.InvariantCulture);

        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Catalogo"] = conexaoCatalogo,
                ["BancoTenant:Servidor"] = admin.Server,
                ["BancoTenant:Porta"] = porta,
                // Espelha o SslMode da conexão administrativa do teste (o padrão de produção é Required — §18).
                ["BancoTenant:SslMode"] = admin.SslMode.ToString(),
                // Só no MySQL de teste/CI sem TLS: permite o runtime (caching_sha2) conectar sobre não-TLS.
                ["BancoTenant:PermitirRecuperacaoChavePublica"] = admin.AllowPublicKeyRetrieval ? "true" : "false",
                ["BancoTenant:AdminUsuario"] = admin.UserID,
                ["BancoTenant:AdminSenhaRef"] = "adm",
                ["BancoTenant:RuntimeUsuario"] = _appUser,
                ["BancoTenant:RuntimeSenhaRef"] = "app",
                ["Segredos:adm"] = admin.Password,
                ["Segredos:app"] = appSenha,
                // Chaves da identidade (Prompt 04): AES-256 (32 bytes) e HMAC, por referência ao "cofre".
                ["SegurancaIdentidade:ChaveProtecaoMfaRef"] = "mfa",
                ["SegurancaIdentidade:ChaveHashRef"] = "hashid",
                ["Segredos:mfa"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Segredos:hashid"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                // Chave HMAC de integridade da trilha de auditoria (Prompt 05).
                ["Auditoria:ChaveIntegridadeRef"] = "auditint",
                ["Segredos:auditint"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            })
            .Build();

        var servicos = new ServiceCollection();
        servicos.AddSingleton<IConfiguration>(configuracao);
        servicos.AddLogging();
        servicos.AddApplication();
        servicos.AddInfrastructure(configuracao, conexaoObrigatoria: true);
        _provedor = servicos.BuildServiceProvider();

        await using (var escopo = _provedor.CreateAsyncScope())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            await catalogo.Database.MigrateAsync();
        }

        Disponivel = true;
    }

    public async Task DisposeAsync()
    {
        if (!Disponivel)
        {
            return;
        }

        var conexaoAdmin = Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN")!;
        await using (var conexao = new MySqlConnection(conexaoAdmin))
        {
            await conexao.OpenAsync();

            // Bases físicas de TODOS os tenants deste run, lidas do catálogo dedicado (o nome vem do NomeTenant
            // escolhido; tenants legados usam tenant_<guid>). Só nomes no padrão seguro são removidos.
            foreach (var nomeBase in await NomesBasesDoCatalogoAsync(conexao, _catalogoDb))
            {
                await ExecutarSeguroAsync(conexao, $"DROP DATABASE IF EXISTS `{nomeBase}`");
            }

            foreach (var id in _tenantsCriados)
            {
                await ExecutarSeguroAsync(conexao, $"DROP DATABASE IF EXISTS `tenant_{id:N}`");
            }

            await ExecutarSeguroAsync(conexao, $"DROP DATABASE IF EXISTS `{_catalogoDb}`");
            await ExecutarSeguroAsync(conexao, $"DROP USER IF EXISTS '{_appUser}'@'%'");
        }

        if (_provedor is not null)
        {
            await _provedor.DisposeAsync();
        }
    }

    /// <summary>Nomes físicos das bases dos tenants registrados no catálogo dedicado do run (padrão seguro apenas).</summary>
    internal static async Task<IReadOnlyList<string>> NomesBasesDoCatalogoAsync(MySqlConnection conexao, string catalogoDb)
    {
        var nomes = new List<string>();
        try
        {
            await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // Nome do catálogo de teste gerado pelo próprio fixture (pb_it_cat_<hex>).
            comando.CommandText = $"SELECT `NomeBaseFisica` FROM `{catalogoDb}`.`tenant`";
#pragma warning restore CA2100
            await using var leitor = await comando.ExecuteReaderAsync();
            while (await leitor.ReadAsync())
            {
                var nome = leitor.GetString(0);
                if (System.Text.RegularExpressions.Regex.IsMatch(nome, "^[a-z][a-z0-9_]{1,63}$"))
                {
                    nomes.Add(nome);
                }
            }
        }
        catch (MySqlException)
        {
            // catálogo sem a tabela (migração falhou): nada a remover
        }

        return nomes;
    }

    private static async Task ExecutarAsync(MySqlConnection conexao, string sql)
    {
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task ExecutarSeguroAsync(MySqlConnection conexao, string sql)
    {
        try
        {
            await ExecutarAsync(conexao, sql);
        }
        catch (MySqlException)
        {
            // limpeza best-effort
        }
    }
}

[CollectionDefinition(Nome)]
public sealed class ColecaoMultiTenant : ICollectionFixture<FixtureMultiTenant>
{
    public const string Nome = "multi-tenant";
}
