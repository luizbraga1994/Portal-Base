using FluentAssertions;

namespace PortalBase.BrowserE2E.Tests.Infra;

/// <summary>
/// Prompt 54 — Fase 28/41: a UI NUNCA deve expor stack trace, connection string, token, segredo ou
/// detalhes internos, mesmo em estado de erro. Estes marcadores jamais devem aparecer em HTML
/// renderizado. Observação: a palavra "password" ocorre legitimamente no formulário de login
/// (type="password"), por isso o controle mira em <b>connection strings</b>, <b>stack traces</b>,
/// <b>tokens</b> e <b>chaves</b> — não em substrings inócuas.
/// </summary>
public static class AfirmacoesSeguranca
{
    private static readonly string[] MarcadoresProibidos =
    [
        "MySqlException",
        "System.NullReferenceException",
        "System.InvalidOperationException",
        "   at PortalBase.",     // linha de stack trace .NET
        "at PortalBase.Infrastructure",
        "Stack trace:",
        "StackTrace",
        "Server=",              // connection string
        "Database=;",
        "Uid=",                 // credencial de conexão
        "Pwd=",
        "ConnectionString",
        "Bearer eyJ",           // JWT
        "-----BEGIN",           // chave privada PEM
        "client_secret",
    ];

    /// <summary>Falha o teste se o HTML renderizado contiver qualquer marcador interno/sensível.</summary>
    public static void DeveEstarLivreDeVazamento(string html, string contexto)
    {
        ArgumentNullException.ThrowIfNull(html);
        foreach (var marcador in MarcadoresProibidos)
        {
            html.Should().NotContain(
                marcador,
                because: $"a UI não pode expor detalhe interno/sensível em '{contexto}' (marcador: {marcador})");
        }
    }
}
