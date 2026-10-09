using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Recriação do catálogo em Development (<see cref="GarantiaCatalogoDesenvolvimento"/>) contra MySQL real, com uma conta
/// MIGRADORA de teste restrita a uma base descartável (o mesmo desenho do configurar-ambiente-local.ps1): base apagada
/// → recriada com a collation do script + todas as migrations; base vazia → tabelas recriadas; em dia → nada muda; sem
/// conta migradora → só orientação; conexão de migração apontando para outra base → recusa sem criar nada.
/// Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
public sealed class GarantiaCatalogoDesenvolvimentoTests : IAsyncLifetime
{
    private readonly string _sufixo = Guid.NewGuid().ToString("N")[..10];
    private readonly string? _admin = Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN");
    private string _base = string.Empty;
    private string _migrador = string.Empty;
    private string _senhaMigrador = string.Empty;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_admin))
        {
            return;
        }

        _base = $"pb_it_gar_{_sufixo}";
        _migrador = $"pb_it_mig_{_sufixo}";
        _senhaMigrador = $"Mg1_{_sufixo}#x";
        await ExecutarAdminAsync($"CREATE USER '{_migrador}'@'%' IDENTIFIED BY '{_senhaMigrador}'");
        await ExecutarAdminAsync($"GRANT ALL PRIVILEGES ON `{_base}`.* TO '{_migrador}'@'%'"); // só esta base (como o script)
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_admin))
        {
            return;
        }

        await ExecutarAdminAsync($"DROP DATABASE IF EXISTS `{_base}`", tolerante: true);
        await ExecutarAdminAsync($"DROP USER IF EXISTS '{_migrador}'@'%'", tolerante: true);
    }

    [SkippableFact]
    public async Task Base_Apagada_E_Recriada_Com_A_Collation_Do_Script_E_Todas_As_Migrations()
    {
        Skip.If(string.IsNullOrWhiteSpace(_admin), "MySQL de teste indisponível.");

        var resultado = await Garantia(comMigrador: true).GarantirAsync();

        resultado.Situacao.Should().Be(SituacaoGarantiaCatalogo.Reparado, resultado.Mensagem);
        resultado.BaseCriada.Should().BeTrue();
        var todas = ContextoAdmin().Database.GetMigrations().Count();
        resultado.MigracoesAplicadas.Should().Be(todas);
        (await EscalarAdminAsync($"SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{_base}'"))
            .Should().Be("utf8mb4_0900_as_cs", "mesma collation do configurar-ambiente-local.ps1 (comparações sensíveis a caixa)");
        await using var contexto = ContextoAdmin();
        (await contexto.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await contexto.Papeis.CountAsync()).Should().BeGreaterThan(0, "o seed de papéis vem nas migrations");
    }

    [SkippableFact]
    public async Task Base_Sem_Tabelas_Tem_As_Tabelas_Recriadas_E_Em_Dia_Nao_Muda_Nada()
    {
        Skip.If(string.IsNullOrWhiteSpace(_admin), "MySQL de teste indisponível.");
        await ExecutarAdminAsync($"CREATE DATABASE `{_base}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs");

        var reparo = await Garantia(comMigrador: true).GarantirAsync();
        reparo.Situacao.Should().Be(SituacaoGarantiaCatalogo.Reparado, reparo.Mensagem);
        reparo.BaseCriada.Should().BeFalse();
        reparo.MigracoesAplicadas.Should().BeGreaterThan(0);

        var deNovo = await Garantia(comMigrador: true).GarantirAsync();
        deNovo.Situacao.Should().Be(SituacaoGarantiaCatalogo.EmDia, deNovo.Mensagem);
        deNovo.MigracoesAplicadas.Should().Be(0);
    }

    [SkippableFact]
    public async Task Sem_Conta_Migradora_So_Orienta_E_Nao_Cria_Nada()
    {
        Skip.If(string.IsNullOrWhiteSpace(_admin), "MySQL de teste indisponível.");

        var resultado = await Garantia(comMigrador: false).GarantirAsync();

        resultado.Situacao.Should().Be(SituacaoGarantiaCatalogo.SemCredencialDeMigracao);
        resultado.Mensagem.Should().Contain("configurar-ambiente-local.ps1");
        (await BaseExisteAsync()).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Conexao_De_Migracao_Para_Outra_Base_E_Recusada_Sem_Criar_Nada()
    {
        Skip.If(string.IsNullOrWhiteSpace(_admin), "MySQL de teste indisponível.");
        var migracaoAlheia = new MySqlConnectionStringBuilder(Migracao()) { Database = _base + "_outra" }.ConnectionString;

        var resultado = await Garantia(comMigrador: true, migracao: migracaoAlheia).GarantirAsync();

        resultado.Situacao.Should().Be(SituacaoGarantiaCatalogo.Indisponivel);
        resultado.Mensagem.Should().NotContain(_senhaMigrador, "a mensagem nunca expõe a senha");
        (await BaseExisteAsync()).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Senha_Errada_Da_Migradora_Vira_Indisponivel_Sem_Expor_Credencial()
    {
        Skip.If(string.IsNullOrWhiteSpace(_admin), "MySQL de teste indisponível.");
        var errada = new MySqlConnectionStringBuilder(Migracao()) { Password = "Senha-Errada#2026" }.ConnectionString;

        var resultado = await Garantia(comMigrador: true, migracao: errada).GarantirAsync();

        resultado.Situacao.Should().Be(SituacaoGarantiaCatalogo.Indisponivel);
        resultado.Mensagem.Should().NotContain("Senha-Errada#2026").And.NotContain(_senhaMigrador);
        (await BaseExisteAsync()).Should().BeFalse();
    }

    // ---------------- helpers ----------------

    private GarantiaCatalogoDesenvolvimento Garantia(bool comMigrador, string? migracao = null)
    {
        var valores = new Dictionary<string, string?> { ["ConnectionStrings:Catalogo"] = Runtime() };
        if (comMigrador)
        {
            valores["ConnectionStrings:CatalogoMigracao"] = migracao ?? Migracao();
        }

        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        return new GarantiaCatalogoDesenvolvimento(configuracao, NullLogger<GarantiaCatalogoDesenvolvimento>.Instance);
    }

    private string Runtime() => new MySqlConnectionStringBuilder(_admin) { Database = _base }.ConnectionString;

    private string Migracao() => new MySqlConnectionStringBuilder(_admin)
    {
        UserID = _migrador,
        Password = _senhaMigrador,
        Database = _base,
    }.ConnectionString;

    private PortalBaseCatalogoDbContext ContextoAdmin()
        => new(new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>().UseMySQL(Runtime()).Options);

    private async Task<bool> BaseExisteAsync()
        => Convert.ToInt64(await EscalarAdminAsync($"SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{_base}'"), CultureInfo.InvariantCulture) > 0;

    private async Task<string?> EscalarAdminAsync(string sql)
    {
        await using var conexao = new MySqlConnection(_admin);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // SQL de teste montado só com identificadores gerados pelo próprio teste.
        comando.CommandText = sql;
#pragma warning restore CA2100
        return Convert.ToString(await comando.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task ExecutarAdminAsync(string sql, bool tolerante = false)
    {
        try
        {
            await using var conexao = new MySqlConnection(_admin);
            await conexao.OpenAsync();
            await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // SQL de teste montado só com identificadores gerados pelo próprio teste.
            comando.CommandText = sql;
#pragma warning restore CA2100
            await comando.ExecuteNonQueryAsync();
        }
        catch (MySqlException) when (tolerante)
        {
            // limpeza best-effort
        }
    }
}
