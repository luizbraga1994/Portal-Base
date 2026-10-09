using Microsoft.Extensions.Logging;

namespace PortalBase.Integration.Tests.Infra;

/// <summary>Logger que captura as mensagens formatadas — prova que token/segredo nunca são logados (regra 8).</summary>
internal sealed class LoggerCaptura<T> : ILogger<T>
{
    public List<string> Mensagens { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => NuloScope.Instancia;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Mensagens.Add(formatter(state, exception));

    private sealed class NuloScope : IDisposable
    {
        public static readonly NuloScope Instancia = new();

        public void Dispose()
        {
        }
    }
}
