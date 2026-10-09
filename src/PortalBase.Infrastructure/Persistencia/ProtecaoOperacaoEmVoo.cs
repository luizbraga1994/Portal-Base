using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace PortalBase.Infrastructure.Persistencia;

/// <summary>
/// P62-R1 (GAP-P62-01) — impede que um <c>DbContext</c> seja DESCARTADO enquanto uma operação dele ainda usa a conexão.
///
/// Causa comprovada: o escopo de DI (circuito Blazor encerrado por navegação/aba fechada, fim de requisição) descarta o
/// <c>DbContext</c> enquanto a consulta de uma página ainda lê o resultado em outra continuação. O descarte do EF fecha a
/// conexão diretamente (<c>RelationalConnection.ResetStateAsync → MySqlConnection.CloseAsync</c>, SEM ponto de
/// interceptação); o <c>Close</c> do MySql.Data encerra o leitor alheio e devolve o canal físico ao POOL — com leitura
/// concorrente, no meio de um pacote — e pode correr em paralelo com o fechamento do próprio EF ao fim da consulta (dupla
/// devolução do mesmo canal ao pool). O próximo usuário do pool (outro escopo, outro usuário, o Worker) recebe bytes da
/// consulta alheia ou trava no ping do pool. Reproduzido isoladamente (Dispose concorrente com ReadAsync ⇒ leitor e
/// próxima abertura travam) e no E2E integrado (hang em <c>MySqlPool.GetPooledConnectionAsync → PingAsync</c>; "Connection
/// must be valid and open", NRE em <c>ReadColumnDataAsync</c>, "NextResult when the reader is closed"). Além de
/// instabilidade, é risco de entregar a um escopo o resultado da consulta de OUTRO escopo.
///
/// Correção (portão por contexto): as operações que usam a conexão — comandos/leitores, abertura/fechamento da conexão
/// pelo EF e operações de transação — são contadas (interceptação do EF). Os contextos do portal
/// (<c>PortalBaseCatalogoDbContext</c>, <c>PortalBaseTenantDbContext</c>) chamam <see cref="AguardarOciosoAsync"/> antes do
/// descarte: o contexto passa a "em descarte", operações NOVAS são recusadas (<see cref="ObjectDisposedException"/>,
/// fail-closed) e um fechamento tardio do próprio EF é suprimido (a conexão é fechada UMA vez, pelo descarte); o descarte
/// aguarda as operações em voo terminarem. Se não terminarem em <see cref="EsperaPadrao"/>, o pool daquela conexão é
/// invalidado (<see cref="MySqlConnection.ClearPool"/>) ANTES do descarte — o canal em uso é fechado em vez de voltar ao
/// pool (o consumidor abandonado recebe erro; ninguém recebe bytes alheios). Não altera isolamento de tenant, não
/// cancela nada; contextos sem operação em voo descartam sem esperar.
/// </summary>
public sealed partial class ProtecaoOperacaoEmVoo : IDbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    /// <summary>Espera máxima pelo término das operações em voo antes do fallback (igual ao CommandTimeout padrão).</summary>
    public static readonly TimeSpan EsperaPadrao = TimeSpan.FromSeconds(30);

    private static readonly object OperacaoConexao = new();
    private static readonly object OperacaoTransacao = new();

    private readonly ConditionalWeakTable<DbContext, EstadoOperacoes> _contextos = new();
    private readonly ConditionalWeakTable<DbCommand, EstadoOperacoes> _comandosEmVoo = new();
    private readonly ILogger<ProtecaoOperacaoEmVoo> _logger;
    private readonly TimeSpan _esperaMaxima;

    public ProtecaoOperacaoEmVoo(ILogger<ProtecaoOperacaoEmVoo> logger)
        : this(logger, EsperaPadrao)
    {
    }

    internal ProtecaoOperacaoEmVoo(ILogger<ProtecaoOperacaoEmVoo> logger, TimeSpan esperaMaxima)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(esperaMaxima, TimeSpan.Zero);
        _logger = logger;
        _esperaMaxima = esperaMaxima;
    }

    /// <summary>A proteção registrada nas opções do contexto (ou <c>null</c> — ex.: fábrica de design-time/migrações).</summary>
    public static ProtecaoOperacaoEmVoo? Localizar(DbContextOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        return opcoes.FindExtension<CoreOptionsExtension>()?.Interceptors?.OfType<ProtecaoOperacaoEmVoo>().FirstOrDefault();
    }

    /// <summary>
    /// Marca o contexto como "em descarte" (novas operações são recusadas) e aguarda, sem bloquear thread, o término das
    /// operações em voo. Chamado pelo contexto ANTES do descarte do EF.
    /// </summary>
    public async ValueTask AguardarOciosoAsync(DbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        var ocioso = Estado(contexto).MarcarDescarte();
        if (ocioso.IsCompleted)
        {
            return;
        }

        try
        {
            await ocioso.WaitAsync(_esperaMaxima).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            InvalidarCanal(contexto);
        }
    }

    /// <summary>Versão síncrona (descarte síncrono do contexto).</summary>
    public void AguardarOcioso(DbContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        var ocioso = Estado(contexto).MarcarDescarte();
        if (!ocioso.IsCompleted && !ocioso.Wait(_esperaMaxima))
        {
            InvalidarCanal(contexto);
        }
    }

    /// <summary>Operações em voo no contexto (diagnóstico/teste).</summary>
    internal int EmVoo(DbContext contexto) =>
        _contextos.TryGetValue(contexto, out var estado) ? estado.EmVoo : 0;

    // ---- Núcleo (internal para testes determinísticos sem MySQL) -----------------------------------------------

    /// <summary>Início de um comando/leitor do contexto (antes do I/O). Recusa se o contexto está em descarte.</summary>
    internal void Iniciar(DbContext? contexto, DbCommand comando, bool execucaoSuprimida)
    {
        ArgumentNullException.ThrowIfNull(comando);
        if (execucaoSuprimida || contexto is null)
        {
            return; // nada vai ao servidor por este comando, ou não há contexto a proteger
        }

        var estado = IniciarOuRecusar(contexto, comando);
        _comandosEmVoo.AddOrUpdate(comando, estado);
    }

    /// <summary>Término do comando (idempotente: fechamento + descarte do leitor, ou falha, concluem UMA vez).</summary>
    internal void Concluir(DbCommand comando)
    {
        ArgumentNullException.ThrowIfNull(comando);
        if (_comandosEmVoo.TryGetValue(comando, out var estado) && _comandosEmVoo.Remove(comando))
        {
            estado.Concluir(comando);
        }
    }

    /// <summary>Abertura da conexão pelo EF (antes do I/O). Recusa se o contexto está em descarte.</summary>
    internal void IniciarAbertura(DbContext? contexto)
    {
        if (contexto is not null)
        {
            IniciarOuRecusar(contexto, OperacaoConexao);
        }
    }

    /// <summary>
    /// Fechamento da conexão pelo próprio EF (fim da consulta). Retorna <c>false</c> quando o contexto está em descarte:
    /// o fechamento deve ser SUPRIMIDO — quem fecha é o descarte, uma única vez, depois de as operações terminarem.
    /// </summary>
    internal bool IniciarFechamento(DbContext? contexto) =>
        contexto is null || Estado(contexto).TentarIniciar(OperacaoConexao);

    /// <summary>Fim da abertura/fechamento (concluído, falho ou cancelado).</summary>
    internal void ConcluirConexao(DbContext? contexto) => ConcluirOperacao(contexto, OperacaoConexao);

    /// <summary>Início de operação de transação (begin/commit/rollback/savepoint). Recusa se o contexto está em descarte.</summary>
    internal void IniciarTransacao(DbContext? contexto)
    {
        if (contexto is not null)
        {
            IniciarOuRecusar(contexto, OperacaoTransacao);
        }
    }

    /// <summary>Fim da operação de transação (concluída ou falha).</summary>
    internal void ConcluirTransacao(DbContext? contexto) => ConcluirOperacao(contexto, OperacaoTransacao);

    // ---- Comandos ----------------------------------------------------------------------------------------------

    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Iniciar(eventData?.Context, command, result.HasResult);
        return result;
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Iniciar(eventData?.Context, command, result.HasResult);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Iniciar(eventData?.Context, command, result.HasResult);
        return result;
    }

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Iniciar(eventData?.Context, command, result.HasResult);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Iniciar(eventData?.Context, command, result.HasResult);
        return result;
    }

    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Iniciar(eventData?.Context, command, result.HasResult);
        return ValueTask.FromResult(result);
    }

    // O EF intercepta DataReaderClosing ANTES de fechar o leitor; o término é o DataReaderDisposing (leitor já fechado).
    public InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        Concluir(command);
        return result;
    }

    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Concluir(command);
        return result;
    }

    public ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Concluir(command);
        return ValueTask.FromResult(result);
    }

    public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Concluir(command);
        return result;
    }

    public ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Concluir(command);
        return ValueTask.FromResult(result);
    }

    public void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Concluir(command);

    public Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Concluir(command);
        return Task.CompletedTask;
    }

    public void CommandCanceled(DbCommand command, CommandEndEventData eventData) => Concluir(command);

    public Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Concluir(command);
        return Task.CompletedTask;
    }

    // ---- Conexão (abertura/fechamento feitos pelo EF) ----------------------------------------------------------

    public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        IniciarAbertura(eventData?.Context);
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        IniciarAbertura(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => ConcluirConexao(eventData?.Context);

    public Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ConcluirConexao(eventData?.Context);
        return Task.CompletedTask;
    }

    public InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
        IniciarFechamento(eventData?.Context) ? result : InterceptionResult.Suppress();

    public ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
        ValueTask.FromResult(IniciarFechamento(eventData?.Context) ? result : InterceptionResult.Suppress());

    public void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => ConcluirConexao(eventData?.Context);

    public Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        ConcluirConexao(eventData?.Context);
        return Task.CompletedTask;
    }

    public void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) => ConcluirConexao(eventData?.Context);

    public Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ConcluirConexao(eventData?.Context);
        return Task.CompletedTask;
    }

    public void ConnectionCanceled(DbConnection connection, ConnectionEndEventData eventData) => ConcluirConexao(eventData?.Context);

    public Task ConnectionCanceledAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ConcluirConexao(eventData?.Context);
        return Task.CompletedTask;
    }

    // ---- Transação (begin/commit/rollback/savepoints fazem I/O na conexão) -------------------------------------

    public InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        IniciarTransacao(eventData?.Context);
        return result;
    }

    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        IniciarTransacao(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        ConcluirTransacao(eventData?.Context);
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        ConcluirTransacao(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        IniciarTransacao(eventData?.Context);
        return result;
    }

    public ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        IniciarTransacao(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => ConcluirTransacao(eventData?.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ConcluirTransacao(eventData?.Context);
        return Task.CompletedTask;
    }

    public InterceptionResult TransactionRollingBack(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        IniciarTransacao(eventData?.Context);
        return result;
    }

    public ValueTask<InterceptionResult> TransactionRollingBackAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        IniciarTransacao(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => ConcluirTransacao(eventData?.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ConcluirTransacao(eventData?.Context);
        return Task.CompletedTask;
    }

    public InterceptionResult CreatingSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        IniciarTransacao(eventData?.Context);
        return result;
    }

    public ValueTask<InterceptionResult> CreatingSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        IniciarTransacao(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) => ConcluirTransacao(eventData?.Context);

    public Task CreatedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        ConcluirTransacao(eventData?.Context);
        return Task.CompletedTask;
    }

    public InterceptionResult RollingBackToSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        IniciarTransacao(eventData?.Context);
        return result;
    }

    public ValueTask<InterceptionResult> RollingBackToSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        IniciarTransacao(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) => ConcluirTransacao(eventData?.Context);

    public Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        ConcluirTransacao(eventData?.Context);
        return Task.CompletedTask;
    }

    public InterceptionResult ReleasingSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        IniciarTransacao(eventData?.Context);
        return result;
    }

    public ValueTask<InterceptionResult> ReleasingSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        IniciarTransacao(eventData?.Context);
        return ValueTask.FromResult(result);
    }

    public void ReleasedSavepoint(DbTransaction transaction, TransactionEventData eventData) => ConcluirTransacao(eventData?.Context);

    public Task ReleasedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        ConcluirTransacao(eventData?.Context);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => ConcluirTransacao(eventData?.Context);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ConcluirTransacao(eventData?.Context);
        return Task.CompletedTask;
    }

    // ---- Infra ------------------------------------------------------------------------------------------------

    private EstadoOperacoes Estado(DbContext contexto) => _contextos.GetValue(contexto, static _ => new EstadoOperacoes());

    private EstadoOperacoes IniciarOuRecusar(DbContext contexto, object operacao)
    {
        var estado = Estado(contexto);
        if (!estado.TentarIniciar(operacao))
        {
            // Fail-closed: o contexto já está sendo descartado (escopo/circuito encerrado) — nenhuma operação nova.
            throw new ObjectDisposedException(contexto.GetType().Name, "Contexto em descarte: operação recusada.");
        }

        return estado;
    }

    private void ConcluirOperacao(DbContext? contexto, object operacao)
    {
        if (contexto is not null && _contextos.TryGetValue(contexto, out var estado))
        {
            estado.Concluir(operacao);
        }
    }

    private void InvalidarCanal(DbContext contexto)
    {
        LogDescarteComOperacaoEmVoo(_logger, (int)_esperaMaxima.TotalSeconds);
        if (contexto.Database.GetDbConnection() is MySqlConnection mysql)
        {
            // O canal ainda em uso é FECHADO no retorno (não volta ao pool); conexões ociosas do mesmo pool são recicladas.
            MySqlConnection.ClearPool(mysql);
        }
    }

    [LoggerMessage(EventId = 6301, Level = LogLevel.Warning,
        Message = "Contexto descartado com operação ainda em voo após {EsperaSegundos}s; pool invalidado para não reutilizar o canal (fail-safe).")]
    private static partial void LogDescarteComOperacaoEmVoo(ILogger logger, int esperaSegundos);

    /// <summary>Operações em voo de UM contexto + estado "em descarte" + sinal de ociosidade.</summary>
    private sealed class EstadoOperacoes
    {
        private readonly Lock _trava = new();
        private readonly HashSet<object> _emVoo = new(ReferenceEqualityComparer.Instance);
        private bool _descartando;
        private TaskCompletionSource? _ocioso;

        public int EmVoo
        {
            get
            {
                lock (_trava)
                {
                    return _emVoo.Count;
                }
            }
        }

        /// <summary>Registra a operação; <c>false</c> se o contexto está em descarte (operação recusada).</summary>
        public bool TentarIniciar(object operacao)
        {
            lock (_trava)
            {
                if (_descartando)
                {
                    return false;
                }

                _emVoo.Add(operacao);
                return true;
            }
        }

        public void Concluir(object operacao)
        {
            TaskCompletionSource? liberar = null;
            lock (_trava)
            {
                if (_emVoo.Remove(operacao) && _emVoo.Count == 0)
                {
                    liberar = _ocioso;
                    _ocioso = null;
                }
            }

            liberar?.TrySetResult();
        }

        /// <summary>Passa a "em descarte" e devolve a tarefa que conclui quando não houver operação em voo.</summary>
        public Task MarcarDescarte()
        {
            lock (_trava)
            {
                _descartando = true;
                return _emVoo.Count == 0
                    ? Task.CompletedTask
                    : (_ocioso ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
    }
}
