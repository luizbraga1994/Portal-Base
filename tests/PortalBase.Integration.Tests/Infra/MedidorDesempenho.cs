using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PortalBase.Integration.Tests.Infra;

/// <summary>
/// MEDIDOR da Onda 5 (gargalos — auditoria de 08/10/2026): conta, para UM banco (pelo nome), os comandos SQL executados
/// pelo EF Core (por tipo) e as gravações (<c>SaveChanges</c>: quantas e o maior número de linhas numa só — o tamanho da
/// maior transação), e mede tempo, memória alocada e o pico do heap gerenciado. Usa o <see cref="DiagnosticListener"/>
/// do EF (sem mexer na composição de DI). Só para testes.
/// </summary>
internal sealed class MedidorDesempenho : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly string _banco;
    private readonly List<IDisposable> _assinaturas = [];
    private readonly Stopwatch _cronometro = Stopwatch.StartNew();
    private readonly long _alocadoInicio = GC.GetTotalAllocatedBytes(precise: true);
    private readonly long _heapInicio;
    private readonly CancellationTokenSource _parar = new();
    private readonly ConcurrentQueue<string> _textos = new();
    private readonly Task _amostragem;
    private long _picoHeap;
    private int _comandos;
    private int _selects;
    private int _inserts;
    private int _updates;
    private int _deletes;
    private int _saveChanges;
    private int _maiorSaveChanges;
    private long _linhasGravadas;
    private long _ticksBanco;

    /// <summary>Soma da duração dos comandos SQL no banco medido (o resto do tempo é da aplicação).</summary>
    public TimeSpan TempoBanco => TimeSpan.FromTicks(Interlocked.Read(ref _ticksBanco));

    public MedidorDesempenho(string banco)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(banco);
        _banco = banco;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _heapInicio = GC.GetTotalMemory(forceFullCollection: true);
        _picoHeap = _heapInicio;
        _assinaturas.Add(DiagnosticListener.AllListeners.Subscribe(this));
        _amostragem = Task.Run(AmostrarAsync);
    }

    /// <summary>Texto dos comandos SQL executados no banco medido (para provar O QUE foi lido, não só quanto).</summary>
    public IReadOnlyList<string> TextosComandos => [.. _textos];

    public Medicao Parar(string cenario)
    {
        _cronometro.Stop();
        _parar.Cancel();
        try
        {
            _amostragem.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        Amostrar();
        return new Medicao(
            cenario, _cronometro.Elapsed, GC.GetTotalAllocatedBytes(precise: true) - _alocadoInicio, Interlocked.Read(ref _picoHeap) - _heapInicio,
            _comandos, _selects, _inserts, _updates, _deletes, _saveChanges, _maiorSaveChanges, Interlocked.Read(ref _linhasGravadas), TempoBanco);
    }

    public void Dispose()
    {
        _parar.Cancel();
        foreach (var assinatura in _assinaturas)
        {
            assinatura.Dispose();
        }

        _parar.Dispose();
    }

    private async Task AmostrarAsync()
    {
        while (!_parar.IsCancellationRequested)
        {
            Amostrar();
            await Task.Delay(20, _parar.Token).ConfigureAwait(false);
        }
    }

    private void Amostrar()
    {
        var atual = GC.GetTotalMemory(forceFullCollection: false);
        long pico;
        do
        {
            pico = Interlocked.Read(ref _picoHeap);
            if (atual <= pico)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _picoHeap, atual, pico) != pico);
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (value.Name == DbLoggerCategory.Name)
        {
            lock (_assinaturas)
            {
                _assinaturas.Add(value.Subscribe(this));
            }
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key == RelationalEventId.CommandExecuted.Name && value.Value is CommandExecutedEventData comando
            && string.Equals(comando.Command.Connection?.Database, _banco, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _comandos);
            var texto = comando.Command.CommandText.TrimStart();
            _textos.Enqueue(texto);
            Interlocked.Add(ref _ticksBanco, comando.Duration.Ticks);
            if (texto.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _selects);
            }
            else if (texto.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _inserts);
            }
            else if (texto.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _updates);
            }
            else if (texto.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _deletes);
            }
        }
        else if (value.Key == CoreEventId.SaveChangesCompleted.Name && value.Value is SaveChangesCompletedEventData gravacao
            && gravacao.Context is { } contexto
            && string.Equals(contexto.Database.GetDbConnection().Database, _banco, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _saveChanges);
            Interlocked.Add(ref _linhasGravadas, gravacao.EntitiesSavedCount);
            int maior;
            do
            {
                maior = _maiorSaveChanges;
                if (gravacao.EntitiesSavedCount <= maior)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref _maiorSaveChanges, gravacao.EntitiesSavedCount, maior) != maior);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }
}

/// <summary>Resultado de uma medição (uma linha da tabela do relatório).</summary>
internal sealed record Medicao(
    string Cenario, TimeSpan Tempo, long BytesAlocados, long PicoHeapBytes, int Comandos, int Selects, int Inserts, int Updates, int Deletes,
    int SaveChanges, int MaiorSaveChanges, long LinhasGravadas, TimeSpan TempoBanco)
{
    public static string Cabecalho =>
        "| Cenário | Tempo | Tempo nos comandos SQL | Alocado | Pico do heap | Comandos SQL (SELECT/INSERT/UPDATE/DELETE) | SaveChanges | Maior SaveChanges (linhas) | Linhas gravadas |\n"
        + "|---|---|---|---|---|---|---|---|---|";

    public string Linha() => string.Create(CultureInfo.InvariantCulture,
        $"| {Cenario} | {Tempo.TotalSeconds:0.0} s | {TempoBanco.TotalSeconds:0.0} s | {BytesAlocados / 1048576.0:0} MB | {PicoHeapBytes / 1048576.0:0} MB | {Comandos} ({Selects}/{Inserts}/{Updates}/{Deletes}) | {SaveChanges} | {MaiorSaveChanges} | {LinhasGravadas} |");
}
