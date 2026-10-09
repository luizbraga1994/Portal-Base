using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using PortalBase.Application.Backup;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Backup;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Opcoes;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Backup;

/// <summary>
/// Restauração aplicada pela tela — operações de base contra MySQL REAL: a troca de tabelas é atômica (produção ↔ backup,
/// a atual fica preservada), as chaves estrangeiras seguem as tabelas, a base de preparo vazia é removida, bases com
/// view ou sem tabelas são recusadas sem mexer na produção, e o <c>GET_LOCK</c> impede duas operações no mesmo alvo.
/// Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
public sealed class OperacoesBaseRestauracaoMySqlTests : IAsyncLifetime
{
    private readonly string? _conexaoAdmin = Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN");
    private readonly string _sufixo = Guid.NewGuid().ToString("N")[..8];
    private readonly List<string> _bases = [];

    private string Producao => $"pb_it_rst_prod_{_sufixo}";

    private string Preparo => $"pb_it_rst_prep_{_sufixo}";

    private string Preservada => $"pb_it_rst_pres_{_sufixo}";

    public Task InitializeAsync()
    {
        _bases.AddRange([Producao, Preparo, Preservada]);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_conexaoAdmin))
        {
            return;
        }

        await using var conexao = await AbrirAsync();
        foreach (var nome in _bases)
        {
            await ExecutarAsync(conexao, $"DROP DATABASE IF EXISTS `{nome}`");
        }
    }

    private OperacoesBaseRestauracaoMySql Operacoes()
    {
        var admin = new MySqlConnectionStringBuilder(_conexaoAdmin);
        var segredos = new ProvedorSegredosFake();
        segredos.Valores["adm"] = admin.Password;
        var opcoes = new OpcoesBancoTenant
        {
            Servidor = admin.Server,
            Porta = admin.Port,
            SslMode = admin.SslMode,
            PermitirRecuperacaoChavePublica = admin.AllowPublicKeyRetrieval,
            AdminUsuario = admin.UserID,
            AdminSenhaRef = "adm",
            RuntimeUsuario = "pb_nao_usado",
            RuntimeSenhaRef = "adm",
        };
        return new OperacoesBaseRestauracaoMySql(new ConstrutorConexaoTenant(Options.Create(opcoes), segredos));
    }

    private async Task<MySqlConnection> AbrirAsync()
    {
        var conexao = new MySqlConnection(_conexaoAdmin);
        await conexao.OpenAsync();
        return conexao;
    }

    private static async Task ExecutarAsync(MySqlConnection conexao, string sql)
    {
        await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // Nomes gerados pelo próprio teste.
        comando.CommandText = sql;
#pragma warning restore CA2100
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<object?> EscalarAsync(MySqlConnection conexao, string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // Nomes gerados pelo próprio teste.
        comando.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor);
        }

        return await comando.ExecuteScalarAsync();
    }

    // Base com cliente(id) ← pedido(cliente_id) (FK) e uma linha marcando a versão dos dados.
    private static async Task CriarBaseAsync(MySqlConnection conexao, string nome, string versao, bool tabelaExtra = false)
    {
        await ExecutarAsync(conexao, $"CREATE DATABASE `{nome}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs");
        await ExecutarAsync(conexao, $"CREATE TABLE `{nome}`.cliente (id INT PRIMARY KEY, versao VARCHAR(20) NOT NULL)");
        await ExecutarAsync(conexao, $"CREATE TABLE `{nome}`.pedido (id INT PRIMARY KEY, cliente_id INT NOT NULL, FOREIGN KEY (cliente_id) REFERENCES cliente(id))");
        await ExecutarAsync(conexao, $"INSERT INTO `{nome}`.cliente VALUES (1, '{versao}')");
        await ExecutarAsync(conexao, $"INSERT INTO `{nome}`.pedido VALUES (10, 1)");
        if (tabelaExtra)
        {
            await ExecutarAsync(conexao, $"CREATE TABLE `{nome}`.criada_depois (id INT PRIMARY KEY)");
        }
    }

    private static async Task<string?> VersaoAsync(MySqlConnection conexao, string nome)
        => (string?)await EscalarAsync(conexao, $"SELECT versao FROM `{nome}`.cliente WHERE id = 1");

    private static async Task<bool> BaseExisteAsync(MySqlConnection conexao, string nome)
        => Convert.ToInt64(await EscalarAsync(conexao, "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @b", ("@b", nome)), CultureInfo.InvariantCulture) == 1;

    private static async Task<long> TabelasAsync(MySqlConnection conexao, string nome)
        => Convert.ToInt64(await EscalarAsync(conexao, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @b", ("@b", nome)), CultureInfo.InvariantCulture);

    [SkippableFact]
    public async Task Troca_LevaOBackupParaProducao_PreservaAAtual_FkSegueATabela()
    {
        Skip.If(string.IsNullOrWhiteSpace(_conexaoAdmin), "MySQL de teste indisponível.");
        await using var conexao = await AbrirAsync();
        await CriarBaseAsync(conexao, Producao, "atual", tabelaExtra: true);
        await CriarBaseAsync(conexao, Preparo, "backup");

        await Operacoes().TrocarTabelasAsync(Producao, Preparo, Preservada);

        (await VersaoAsync(conexao, Producao)).Should().Be("backup");
        (await VersaoAsync(conexao, Preservada)).Should().Be("atual");
        (await TabelasAsync(conexao, Producao)).Should().Be(2);          // só as tabelas do backup
        (await TabelasAsync(conexao, Preservada)).Should().Be(3);        // tudo o que havia, inclusive a tabela extra
        (await BaseExisteAsync(conexao, Preparo)).Should().BeFalse();    // preparo vazio removido

        // A FK da produção aponta para a produção (seguiu a tabela), e a da preservada para a preservada.
        var referenciaProducao = await EscalarAsync(conexao,
            "SELECT REFERENCED_TABLE_SCHEMA FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = @b AND TABLE_NAME = 'pedido' AND REFERENCED_TABLE_NAME IS NOT NULL",
            ("@b", Producao));
        referenciaProducao.Should().Be(Producao);
        var referenciaPreservada = await EscalarAsync(conexao,
            "SELECT REFERENCED_TABLE_SCHEMA FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = @b AND TABLE_NAME = 'pedido' AND REFERENCED_TABLE_NAME IS NOT NULL",
            ("@b", Preservada));
        referenciaPreservada.Should().Be(Preservada);
    }

    [SkippableFact]
    public async Task Troca_BaseRestauradaSemTabelas_Recusada_ProducaoIntacta()
    {
        Skip.If(string.IsNullOrWhiteSpace(_conexaoAdmin), "MySQL de teste indisponível.");
        await using var conexao = await AbrirAsync();
        await CriarBaseAsync(conexao, Producao, "atual");
        await ExecutarAsync(conexao, $"CREATE DATABASE `{Preparo}`");

        var acao = async () => await Operacoes().TrocarTabelasAsync(Producao, Preparo, Preservada);

        await acao.Should().ThrowAsync<InvalidOperationException>();
        (await VersaoAsync(conexao, Producao)).Should().Be("atual");
        (await BaseExisteAsync(conexao, Preservada)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Troca_ProducaoComView_Recusada_SemMexerEmNada()
    {
        Skip.If(string.IsNullOrWhiteSpace(_conexaoAdmin), "MySQL de teste indisponível.");
        await using var conexao = await AbrirAsync();
        await CriarBaseAsync(conexao, Producao, "atual");
        await ExecutarAsync(conexao, $"CREATE VIEW `{Producao}`.v_cliente AS SELECT id FROM `{Producao}`.cliente");
        await CriarBaseAsync(conexao, Preparo, "backup");

        var acao = async () => await Operacoes().TrocarTabelasAsync(Producao, Preparo, Preservada);

        await acao.Should().ThrowAsync<InvalidOperationException>();
        (await VersaoAsync(conexao, Producao)).Should().Be("atual");
        (await VersaoAsync(conexao, Preparo)).Should().Be("backup");
        (await BaseExisteAsync(conexao, Preservada)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Troca_PreservadaJaExiste_FalhaSemMexerNaProducao()
    {
        Skip.If(string.IsNullOrWhiteSpace(_conexaoAdmin), "MySQL de teste indisponível.");
        await using var conexao = await AbrirAsync();
        await CriarBaseAsync(conexao, Producao, "atual");
        await CriarBaseAsync(conexao, Preparo, "backup");
        await CriarBaseAsync(conexao, Preservada, "outra");

        var acao = async () => await Operacoes().TrocarTabelasAsync(Producao, Preparo, Preservada);

        await acao.Should().ThrowAsync<MySqlException>();
        (await VersaoAsync(conexao, Producao)).Should().Be("atual");
        (await VersaoAsync(conexao, Preservada)).Should().Be("outra"); // nunca removida: não é dela
    }

    [SkippableTheory]
    [InlineData("tenant`; DROP DATABASE mysql; --")]
    [InlineData("mysql")]
    [InlineData("Tenant_Maiusculo")]
    public async Task Troca_NomeDeBaseHostil_RecusadoAntesDeConectar(string hostil)
    {
        Skip.If(string.IsNullOrWhiteSpace(_conexaoAdmin), "MySQL de teste indisponível.");

        var acao = async () => await Operacoes().TrocarTabelasAsync(hostil, Preparo, Preservada);

        await acao.Should().ThrowAsync<EntradaInvalidaException>();
    }

    [SkippableFact]
    public async Task Troca_BasesRepetidas_Recusada()
    {
        Skip.If(string.IsNullOrWhiteSpace(_conexaoAdmin), "MySQL de teste indisponível.");

        var acao = async () => await Operacoes().TrocarTabelasAsync(Producao, Producao, Preservada);

        await acao.Should().ThrowAsync<ArgumentException>();
    }

    [SkippableFact]
    public async Task Bloqueio_SegundaOperacaoNoMesmoAlvo_Recusada_AteLiberar()
    {
        Skip.If(string.IsNullOrWhiteSpace(_conexaoAdmin), "MySQL de teste indisponível.");
        var chave = $"pb_it_lock_{_sufixo}";
        var operacoes = Operacoes();

        var primeiro = await operacoes.BloquearAsync(chave);
        var segundo = async () => await operacoes.BloquearAsync(chave);
        await segundo.Should().ThrowAsync<OperacaoBackupEmAndamentoException>();

        // Outro alvo não é afetado.
        await using (await operacoes.BloquearAsync(chave + "_outro"))
        {
        }

        await primeiro.DisposeAsync();
        await using var terceiro = await operacoes.BloquearAsync(chave);
        terceiro.Should().NotBeNull();
    }

    [SkippableTheory]
    [InlineData("Chave-Invalida")]
    [InlineData("x")]
    [InlineData("pb'; SELECT 1; --")]
    public async Task Bloqueio_ChaveForaDoPadrao_Recusada(string chave)
    {
        Skip.If(string.IsNullOrWhiteSpace(_conexaoAdmin), "MySQL de teste indisponível.");

        var acao = async () => await Operacoes().BloquearAsync(chave);

        await acao.Should().ThrowAsync<ArgumentException>();
    }
}
