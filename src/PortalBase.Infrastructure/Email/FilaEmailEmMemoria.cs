using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortalBase.Application.Notificacoes;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// Fila de disparo em memória (limitada). O conteúdo (que pode ter um link de uso único) fica só na memória do
/// processo, nunca em banco ou log; se o processo cair, o pedido se perde e o usuário pede de novo — aceitável para
/// e-mails transacionais curtos como a redefinição de senha.
/// </summary>
public sealed partial class FilaEmailEmMemoria : IFilaEmail, IEstatisticasFilaEmail
{
    public const int Capacidade = 1000;

    private readonly Channel<MensagemEmail> _canal = Channel.CreateBounded<MensagemEmail>(
        new BoundedChannelOptions(Capacidade) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly IOptions<OpcoesEmail> _opcoes;
    private readonly ILogger<FilaEmailEmMemoria> _logger;
    private long _naoEnviados;

    public FilaEmailEmMemoria(IOptions<OpcoesEmail> opcoes, ILogger<FilaEmailEmMemoria> logger)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(logger);
        _opcoes = opcoes;
        _logger = logger;
    }

    /// <summary>Lido a cada pedido: ligar/desligar o e-mail pela tela vale na hora.</summary>
    public bool Habilitado => _opcoes.Value.Habilitado;

    internal ChannelReader<MensagemEmail> Leitor => _canal.Reader;

    /// <summary>E-mails que não saíram desde que o processo subiu (FILA-001) — a rotina de alertas acompanha.</summary>
    public long NaoEnviados => Interlocked.Read(ref _naoEnviados);

    public int Pendentes => _canal.Reader.Count;

    /// <summary>Conta um e-mail que não saiu (falha final do transporte, fila cheia, perdido no desligamento).</summary>
    internal void RegistrarNaoEnviados(int quantidade = 1) => Interlocked.Add(ref _naoEnviados, Math.Max(0, quantidade));

    public bool Enfileirar(MensagemEmail mensagem)
    {
        ArgumentNullException.ThrowIfNull(mensagem);
        if (!Habilitado)
        {
            LogDesligado(_logger);
            return false;
        }

        if (_canal.Writer.TryWrite(mensagem))
        {
            return true;
        }

        RegistrarNaoEnviados();
        LogFilaCheia(_logger, Capacidade);
        return false;
    }

    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning, Message = "E-mail não enfileirado: o envio está desligado (Configurações → E-mail ou Email:Modo=Desligado).")]
    private static partial void LogDesligado(ILogger logger);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Error, Message = "E-mail descartado: fila de disparo cheia ({Capacidade} mensagens).")]
    private static partial void LogFilaCheia(ILogger logger, int capacidade);
}
