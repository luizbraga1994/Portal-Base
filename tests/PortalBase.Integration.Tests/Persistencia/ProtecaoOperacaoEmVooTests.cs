using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using PortalBase.Infrastructure.Persistencia;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// P62-R1 (GAP-P62-01) — causa raiz da instabilidade do E2E integrado: o descarte do <c>DbContext</c> (fim do escopo do
/// circuito Blazor/requisição) enquanto uma leitura ainda estava em voo devolvia ao pool do MySql.Data um canal no meio
/// do protocolo — o próximo usuário do pool travava no ping ou recebia bytes da consulta alheia. Estes testes provam,
/// com MySQL REAL, que <see cref="ProtecaoOperacaoEmVoo"/> faz o descarte aguardar a leitura e mantém o pool íntegro,
/// e que uma operação abandonada além do limite NÃO volta ao pool (fail-safe). Os testes de contador não exigem MySQL.
/// </summary>
public sealed class ProtecaoOperacaoEmVooTests
{
    private const int TotalLinhas = 100_000;
    private static readonly TimeSpan Limite = TimeSpan.FromSeconds(60);

    // 100.000 linhas × ~200 bytes, sem acessar tabela alguma (produto cartesiano de dígitos): a leitura leva tempo
    // suficiente para o descarte acontecer com o leitor ABERTO e bytes ainda chegando.
    private const string SqlMuitasLinhas =
        "SELECT REPEAT('x', 200) AS Value FROM " +
        "(SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) a " +
        "CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) b " +
        "CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) c " +
        "CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) d " +
        "CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) e";

    // ---- MySQL real ------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Descarte_Durante_Leitura_Aguarda_A_Leitura_E_Mantem_O_Pool_Integro()
    {
        var conexao = ConexaoComPoolExclusivo();
        try
        {
            var opcoes = Opcoes(conexao, new ProtecaoOperacaoEmVoo(NullLogger<ProtecaoOperacaoEmVoo>.Instance));
            var contexto = new PortalBaseCatalogoDbContext(opcoes);
            var primeiraLinha = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var leitura = Task.Run(async () =>
            {
                var linhas = 0;
                await foreach (var _ in contexto.Database.SqlQueryRaw<string>(SqlMuitasLinhas).AsAsyncEnumerable())
                {
                    if (++linhas == 1)
                    {
                        primeiraLinha.SetResult();
                        await Task.Delay(300); // o descarte acontece AQUI, com o leitor aberto
                    }
                }

                return linhas;
            });

            await primeiraLinha.Task.WaitAsync(Limite);
            var descarte = contexto.DisposeAsync().AsTask(); // escopo encerrado no meio da leitura (navegação/aba fechada)
            descarte.IsCompleted.Should().BeFalse("o descarte da conexão aguarda o leitor em voo");

            (await leitura.WaitAsync(Limite)).Should().Be(TotalLinhas, "a leitura em voo termina íntegra");
            await descarte.WaitAsync(Limite);

            // Pool de UM canal: a próxima consulta reutiliza o MESMO canal e recebe o SEU resultado (sem hang/sem lixo).
            await using var proximo = new PortalBaseCatalogoDbContext(opcoes);
            (await proximo.Database.SqlQueryRaw<int>("SELECT 4242 AS Value").SingleAsync().WaitAsync(Limite))
                .Should().Be(4242);
        }
        finally
        {
            LimparPool(conexao);
        }
    }

    [SkippableFact]
    public async Task Operacao_Abandonada_Alem_Do_Limite_Nao_Devolve_O_Canal_Ao_Pool()
    {
        var conexao = ConexaoComPoolExclusivo();
        var liberarConsumidor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? leitura = null;
        try
        {
            var protecao = new ProtecaoOperacaoEmVoo(NullLogger<ProtecaoOperacaoEmVoo>.Instance, TimeSpan.FromMilliseconds(300));
            var opcoes = Opcoes(conexao, protecao);
            var contexto = new PortalBaseCatalogoDbContext(opcoes);
            var canalOriginal = -1;
            var primeiraLinha = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            leitura = Task.Run(async () =>
            {
                await foreach (var _ in contexto.Database.SqlQueryRaw<string>(SqlMuitasLinhas).AsAsyncEnumerable())
                {
                    canalOriginal = ((MySqlConnection)contexto.Database.GetDbConnection()).ServerThread;
                    primeiraLinha.TrySetResult();
                    await liberarConsumidor.Task; // consumidor "abandonado": não lê nem descarta o leitor
                }
            });

            await primeiraLinha.Task.WaitAsync(Limite);
            await contexto.DisposeAsync().AsTask().WaitAsync(Limite); // não trava: espera o limite e aplica o fallback

            await using var proximo = new PortalBaseCatalogoDbContext(opcoes);
            await proximo.Database.OpenConnectionAsync().WaitAsync(Limite);
            var canalNovo = ((MySqlConnection)proximo.Database.GetDbConnection()).ServerThread;
            canalNovo.Should().NotBe(canalOriginal, "o canal possivelmente inconsistente NÃO pode voltar ao pool");
            (await proximo.Database.SqlQueryRaw<int>("SELECT 4242 AS Value").SingleAsync().WaitAsync(Limite))
                .Should().Be(4242);
        }
        finally
        {
            liberarConsumidor.TrySetResult();
            if (leitura is not null)
            {
                // O consumidor abandonado recebe ERRO (canal fechado) ou termina — nunca fica pendurado.
                var concluiu = await Task.WhenAny(leitura, Task.Delay(Limite)) == leitura;
                concluiu.Should().BeTrue("o consumidor abandonado não pode ficar travado");
            }

            LimparPool(conexao);
        }
    }

    // ---- Contador + descarte do contexto (sem MySQL: o contexto nunca abre conexão) ---------------------------

    [Fact]
    public async Task Contexto_Sem_Operacao_Em_Voo_Descarta_Sem_Esperar()
    {
        var protecao = NovaProtecao();
        var contexto = ContextoSemConexao(protecao);

        var descarte = contexto.DisposeAsync().AsTask();

        descarte.IsCompleted.Should().BeTrue();
        await descarte;
    }

    [Fact]
    public async Task Descarte_Do_Contexto_Aguarda_O_Leitor_E_Conclusao_E_Idempotente()
    {
        var protecao = NovaProtecao();
        var contexto = ContextoSemConexao(protecao);
        await using var comando = new MySqlCommand("SELECT 1");

        protecao.Iniciar(contexto, comando, execucaoSuprimida: false);
        protecao.Iniciar(contexto, comando, execucaoSuprimida: false); // reentrada do MESMO comando não conta duas vezes
        protecao.EmVoo(contexto).Should().Be(1);

        var descarte = contexto.DisposeAsync().AsTask();
        descarte.IsCompleted.Should().BeFalse("há leitor em voo: o contexto não pode fechar a conexão");

        protecao.Concluir(comando); // DataReaderClosing
        protecao.Concluir(comando); // DataReaderDisposing posterior não decrementa de novo
        await descarte.WaitAsync(Limite);
        protecao.EmVoo(contexto).Should().Be(0);
    }

    [Fact]
    public async Task Descarte_Sincrono_Tambem_Aguarda_O_Termino()
    {
        var protecao = NovaProtecao();
        var contexto = ContextoSemConexao(protecao);
        using var comando = new MySqlCommand("SELECT 1");
        protecao.Iniciar(contexto, comando, execucaoSuprimida: false);

        // Sem "using": o Concluir libera o descarte (TrySetResult) ANTES de a continuação terminar; descartar a Task
        // ainda em execução lançava InvalidOperationException em runner carregado (CI 37055098592). Espera-se o fim.
        var termino = Task.Delay(200).ContinueWith(_ => protecao.Concluir(comando), TaskScheduler.Default);
        var inicio = DateTimeOffset.UtcNow;
        contexto.Dispose(); // descarte SÍNCRONO (o objeto do teste) — bloqueia até a operação em voo concluir

        (DateTimeOffset.UtcNow - inicio).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));
        protecao.EmVoo(contexto).Should().Be(0);
        await termino.WaitAsync(Limite); // a conclusão simulada termina antes de o teste sair
    }

    [Fact]
    public async Task Varias_Operacoes_So_Liberam_O_Descarte_Quando_Todas_Terminam()
    {
        var protecao = NovaProtecao();
        var contexto = ContextoSemConexao(protecao);
        await using var leitor = new MySqlCommand("SELECT 1");
        await using var comando = new MySqlCommand("UPDATE x SET y = 1");
        await using var escalar = new MySqlCommand("SELECT COUNT(*)");

        protecao.Iniciar(contexto, leitor, execucaoSuprimida: false);
        protecao.Iniciar(contexto, comando, execucaoSuprimida: false);
        protecao.Iniciar(contexto, escalar, execucaoSuprimida: false);
        protecao.EmVoo(contexto).Should().Be(3);

        var descarte = contexto.DisposeAsync().AsTask();
        protecao.Concluir(comando); // NonQueryExecuted / CommandFailed
        protecao.Concluir(escalar); // ScalarExecuted / CommandCanceled
        descarte.IsCompleted.Should().BeFalse("ainda há um leitor em voo");

        protecao.Concluir(leitor);
        await descarte.WaitAsync(Limite);
    }

    [Fact]
    public void Execucao_Suprimida_Ou_Sem_Contexto_Nao_Conta()
    {
        var protecao = NovaProtecao();
        var contexto = ContextoSemConexao(protecao);
        using var comando = new MySqlCommand("SELECT 1");

        protecao.Iniciar(contexto, comando, execucaoSuprimida: true);
        protecao.Iniciar(null, comando, execucaoSuprimida: false);

        protecao.EmVoo(contexto).Should().Be(0);
        contexto.Dispose();
    }

    [Fact]
    public async Task Em_Descarte_Operacoes_Novas_Sao_Recusadas_E_Fechamento_Tardio_E_Suprimido()
    {
        var protecao = NovaProtecao();
        var contexto = ContextoSemConexao(protecao);
        await using var emVoo = new MySqlCommand("SELECT 1");
        await using var tardio = new MySqlCommand("SELECT 2");
        protecao.Iniciar(contexto, emVoo, execucaoSuprimida: false);

        var descarte = contexto.DisposeAsync().AsTask(); // contexto passa a "em descarte"

        // Fail-closed: nada novo usa a conexão de um contexto em descarte (escopo/circuito encerrado).
        var comando = () => protecao.Iniciar(contexto, tardio, execucaoSuprimida: false);
        comando.Should().Throw<ObjectDisposedException>();
        var abertura = () => protecao.IniciarAbertura(contexto);
        abertura.Should().Throw<ObjectDisposedException>();
        var transacao = () => protecao.IniciarTransacao(contexto);
        transacao.Should().Throw<ObjectDisposedException>();

        // O fechamento do próprio EF ao fim da leitura é SUPRIMIDO: quem fecha é o descarte, uma única vez.
        protecao.IniciarFechamento(contexto).Should().BeFalse();

        descarte.IsCompleted.Should().BeFalse("a leitura iniciada antes do descarte continua em voo");
        protecao.Concluir(emVoo);
        await descarte.WaitAsync(Limite);
    }

    [Fact]
    public async Task Abertura_Fechamento_E_Transacao_Em_Voo_Seguram_O_Descarte()
    {
        var protecao = NovaProtecao();
        var contexto = ContextoSemConexao(protecao);

        protecao.IniciarAbertura(contexto);            // OpenAsync em voo
        protecao.IniciarTransacao(contexto);           // COMMIT em voo
        protecao.EmVoo(contexto).Should().Be(2);

        var descarte = contexto.DisposeAsync().AsTask();
        protecao.ConcluirConexao(contexto);            // ConnectionOpened/Failed/Canceled
        descarte.IsCompleted.Should().BeFalse("o COMMIT ainda está em voo");
        protecao.ConcluirTransacao(contexto);          // TransactionCommitted/Failed
        await descarte.WaitAsync(Limite);
    }

    [Fact]
    public void Fechamento_Do_EF_Fora_De_Descarte_E_Permitido_E_Contado()
    {
        var protecao = NovaProtecao();
        var contexto = ContextoSemConexao(protecao);

        protecao.IniciarFechamento(contexto).Should().BeTrue();
        protecao.EmVoo(contexto).Should().Be(1, "o fechamento do EF em andamento segura um descarte concorrente");
        protecao.ConcluirConexao(contexto);
        protecao.EmVoo(contexto).Should().Be(0);
        contexto.Dispose();
    }

    [Fact]
    public void Protecao_E_Localizada_Nas_Opcoes_E_Ausente_Fora_Delas()
    {
        var protecao = NovaProtecao();
        var com = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>().UseMySQL(ConexaoFicticia).AddInterceptors(protecao).Options;
        var sem = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>().UseMySQL(ConexaoFicticia).Options;

        ProtecaoOperacaoEmVoo.Localizar(com).Should().BeSameAs(protecao);
        ProtecaoOperacaoEmVoo.Localizar(sem).Should().BeNull("design-time/migrações não registram a proteção");
    }

    [Fact]
    public void Espera_Maxima_Invalida_E_Rejeitada()
    {
        var acao = () => new ProtecaoOperacaoEmVoo(NullLogger<ProtecaoOperacaoEmVoo>.Instance, TimeSpan.Zero);
        acao.Should().Throw<ArgumentOutOfRangeException>();
    }

    private const string ConexaoFicticia = "Server=127.0.0.1;Port=1;Database=nao_conecta";

    private static ProtecaoOperacaoEmVoo NovaProtecao() => new(NullLogger<ProtecaoOperacaoEmVoo>.Instance);

    private static PortalBaseCatalogoDbContext ContextoSemConexao(ProtecaoOperacaoEmVoo protecao) =>
        new(new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>().UseMySQL(ConexaoFicticia).AddInterceptors(protecao).Options);

    // ---- Infra -----------------------------------------------------------------------------------------------

    private static string ConexaoComPoolExclusivo()
    {
        var admin = Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN");
        Skip.If(string.IsNullOrWhiteSpace(admin), "MySQL de teste indisponível (PORTALBASE_TEST_MYSQL_ADMIN ausente).");

        // A chave do pool do MySql.Data é a connection string: um ConnectionLifeTime aleatório isola o pool deste teste,
        // e MaximumPoolSize=1 obriga a próxima consulta a reutilizar o MESMO canal (é o que o teste precisa provar).
        return new MySqlConnectionStringBuilder(admin)
        {
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = 1,
            ConnectionLifeTime = (uint)Random.Shared.Next(3_600, 7_200),
            Database = string.Empty,
        }.ConnectionString;
    }

    private static DbContextOptions<PortalBaseCatalogoDbContext> Opcoes(string conexao, ProtecaoOperacaoEmVoo protecao) =>
        new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>()
            .UseMySQL(conexao)
            .AddInterceptors(protecao)
            .Options;

    private static void LimparPool(string conexao)
    {
        using var conexaoPool = new MySqlConnection(conexao);
        MySqlConnection.ClearPool(conexaoPool);
    }
}
