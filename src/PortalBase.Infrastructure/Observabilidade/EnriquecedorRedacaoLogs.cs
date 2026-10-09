using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace PortalBase.Infrastructure.Observabilidade;

/// <summary>
/// Defesa em profundidade da regra 8: antes de qualquer saída de log, mascara valores de parâmetros sensíveis que
/// apareçam em propriedades de texto (ex.: a URL do link de redefinição de senha registrada pelo log de requisições
/// do ASP.NET: <c>?token=...</c> vira <c>?token=***</c>). Usado pela Web e pelo Worker; o <see cref="SinkLogEventosWindows"/>
/// aplica o mesmo <see cref="Mascarar"/> ao texto final (inclusive à mensagem de exceção).
/// </summary>
public sealed partial class EnriquecedorRedacaoLogs : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var propriedade in logEvent.Properties.ToList())
        {
            if (propriedade.Value is ScalarValue { Value: string texto } && ParametroSensivel().IsMatch(texto))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(propriedade.Key, new ScalarValue(Mascarar(texto))));
            }
        }
    }

    public static string Mascarar(string texto) => ParametroSensivel().Replace(texto, "$1***");

    [GeneratedRegex(@"((?:^|[?&;\s])(?:token|codigo|senha|password|code)=)[^&\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParametroSensivel();
}
