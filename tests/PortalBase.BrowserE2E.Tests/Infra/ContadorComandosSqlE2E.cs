using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PortalBase.BrowserE2E.Tests.Infra;

/// <summary>
/// Conta os comandos SQL que o EF Core da Web (no MESMO processo do teste) executa em UM banco (pelo nome), guardando o
/// texto — para medir o que uma navegação de verdade faz no banco (Onda 5: UI-001 prerender, UI-005 logo). Só teste.
/// </summary>
internal sealed class ContadorComandosSqlE2E : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly string _banco;
    private readonly List<IDisposable> _assinaturas = [];
    private readonly ConcurrentQueue<string> _textos = new();

    public ContadorComandosSqlE2E(string banco)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(banco);
        _banco = banco;
        _assinaturas.Add(DiagnosticListener.AllListeners.Subscribe(this));
    }

    public IReadOnlyList<string> Textos => [.. _textos];

    public void Limpar() => _textos.Clear();

    public void Dispose()
    {
        lock (_assinaturas)
        {
            foreach (var assinatura in _assinaturas)
            {
                assinatura.Dispose();
            }

            _assinaturas.Clear();
        }
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
            _textos.Enqueue(comando.Command.CommandText.TrimStart());
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
