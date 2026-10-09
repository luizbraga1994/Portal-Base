using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace PortalBase.Infrastructure.Observabilidade;

/// <summary>Tipo da entrada no Log de Aplicativo do Windows.</summary>
public enum TipoEventoWindows
{
    Aviso,
    Erro,
}

/// <summary>Grava uma entrada no Log de Aplicativo do Windows (porta para os testes não dependerem do Windows).</summary>
public interface IGravadorLogEventosWindows
{
    void Gravar(TipoEventoWindows tipo, int idEvento, string texto);
}

/// <summary>
/// Grava no Log de Aplicativo com a ORIGEM informada ("PortalBase.Web"/"PortalBase.Worker"), registrada como
/// administrador pelo <c>scripts/configurar-iis.ps1</c>. Origem não registrada e conta sem permissão para registrá-la ⇒
/// o Windows recusa (o <see cref="SinkLogEventosWindows"/> então se desliga, sem derrubar o processo).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GravadorLogEventosWindows : IGravadorLogEventosWindows
{
    private readonly string _origem;

    public GravadorLogEventosWindows(string origem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origem);
        _origem = origem;
    }

    public void Gravar(TipoEventoWindows tipo, int idEvento, string texto)
        => EventLog.WriteEntry(_origem, texto, tipo == TipoEventoWindows.Erro ? EventLogEntryType.Error : EventLogEntryType.Warning, idEvento);
}

/// <summary>
/// Servidor próprio (Windows): grava AVISOS e ERROS no Log de Aplicativo do Windows (Visualizador de Eventos). O Worker
/// como serviço do Windows e a Web no IIS não têm console — sem isto um erro só apareceria como "o serviço parou".
/// Regra 8: o texto final (mensagem + exceção) passa pela mesma redação dos logs (<see cref="EnriquecedorRedacaoLogs.Mascarar"/>).
/// Nunca derruba o processo: se o Windows recusar a gravação, o sink se desliga até o processo reiniciar.
/// </summary>
public sealed class SinkLogEventosWindows : ILogEventSink
{
    /// <summary>O Log de Aplicativo aceita até 31.839 caracteres por entrada; o resto é cortado com aviso.</summary>
    public const int TamanhoMaximo = 31_000;

    public const int IdFatal = 1001;
    public const int IdErro = 1002;
    public const int IdAviso = 1003;

    private readonly IGravadorLogEventosWindows _gravador;
    private readonly LogEventLevel _nivelMinimo;
    private volatile bool _desligado;

    public SinkLogEventosWindows(IGravadorLogEventosWindows gravador, LogEventLevel nivelMinimo = LogEventLevel.Warning)
    {
        ArgumentNullException.ThrowIfNull(gravador);
        _gravador = gravador;
        _nivelMinimo = nivelMinimo < LogEventLevel.Warning ? LogEventLevel.Warning : nivelMinimo;
    }

    /// <summary>Verdadeiro depois que o Windows recusou uma gravação (origem não registrada, sem permissão...).</summary>
    public bool Desligado => _desligado;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (_desligado || logEvent.Level < _nivelMinimo)
        {
            return;
        }

        var (tipo, id) = logEvent.Level switch
        {
            LogEventLevel.Fatal => (TipoEventoWindows.Erro, IdFatal),
            LogEventLevel.Error => (TipoEventoWindows.Erro, IdErro),
            _ => (TipoEventoWindows.Aviso, IdAviso),
        };

        try
        {
            _gravador.Gravar(tipo, id, Formatar(logEvent));
        }
#pragma warning disable CA1031 // Log nunca derruba o processo: qualquer recusa do Windows desliga só este destino.
        catch (Exception)
#pragma warning restore CA1031
        {
            _desligado = true;
        }
    }

    /// <summary>"[Origem do log] mensagem", a exceção completa em seguida; redigido e limitado a <see cref="TamanhoMaximo"/>.</summary>
    public static string Formatar(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var texto = new StringBuilder();
        if (logEvent.Properties.TryGetValue("SourceContext", out var contexto) && contexto is ScalarValue { Value: string origem })
        {
            texto.Append('[').Append(origem).Append("] ");
        }

        texto.Append(logEvent.RenderMessage(CultureInfo.InvariantCulture));
        if (logEvent.Exception is not null)
        {
            texto.AppendLine().AppendLine().Append(logEvent.Exception);
        }

        var redigido = EnriquecedorRedacaoLogs.Mascarar(texto.ToString());
        return redigido.Length <= TamanhoMaximo
            ? redigido
            : string.Concat(redigido.AsSpan(0, TamanhoMaximo), Environment.NewLine, "[... texto cortado; o restante está no log do servidor]");
    }
}

/// <summary>Liga o <see cref="SinkLogEventosWindows"/> nos hosts (Web e Worker) quando configurado.</summary>
public static class LogEventosWindows
{
    /// <summary>Gravado como <c>true</c> pelo <c>scripts/configurar-iis.ps1</c> no pool do IIS e no serviço do Worker.</summary>
    public const string ChaveHabilitado = "Observabilidade:LogEventosWindows:Habilitado";

    /// <summary>Só no Windows e só com <see cref="ChaveHabilitado"/> = true (desligado por padrão: testes e Linux nunca gravam).</summary>
    public static bool Habilitado(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        return OperatingSystem.IsWindows() && configuracao.GetValue<bool>(ChaveHabilitado);
    }

    /// <summary>Acrescenta o Log de Aplicativo do Windows (avisos e erros, origem <paramref name="origem"/>) se habilitado.</summary>
    public static LoggerConfiguration GravarNoLogEventosWindowsSeHabilitado(
        this LoggerConfiguration configuracaoLog, IConfiguration configuracao, string origem)
    {
        ArgumentNullException.ThrowIfNull(configuracaoLog);
        ArgumentException.ThrowIfNullOrWhiteSpace(origem);
        if (!Habilitado(configuracao) || !OperatingSystem.IsWindows()) // repetido para o analisador de plataforma (CA1416)
        {
            return configuracaoLog;
        }

        return configuracaoLog.WriteTo.Sink(new SinkLogEventosWindows(new GravadorLogEventosWindows(origem)));
    }

    /// <summary>
    /// Falha fatal ANTES do log configurado existir (ex.: a pré-checagem de produção do Worker). Só no Windows; nunca
    /// lança (o chamador já está tratando uma falha).
    /// </summary>
    public static void RegistrarFalhaFatal(string origem, Exception excecao, string mensagem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origem);
        ArgumentNullException.ThrowIfNull(excecao);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RegistrarFalhaFatal(new GravadorLogEventosWindows(origem), excecao, mensagem);
    }

    /// <summary>Mesma gravação, com o gravador informado (testável fora do Windows).</summary>
    public static void RegistrarFalhaFatal(IGravadorLogEventosWindows gravador, Exception excecao, string mensagem)
    {
        ArgumentNullException.ThrowIfNull(gravador);
        ArgumentNullException.ThrowIfNull(excecao);
        var modelo = new MessageTemplateParser().Parse(TextoLiteral(mensagem));
        new SinkLogEventosWindows(gravador).Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Fatal, excecao, modelo, []));
    }

    // A mensagem é texto fixo, não modelo: chaves viram literais.
    private static string TextoLiteral(string mensagem)
        => (mensagem ?? string.Empty).Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
}
