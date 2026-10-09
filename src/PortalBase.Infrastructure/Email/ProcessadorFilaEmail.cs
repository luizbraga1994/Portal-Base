using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortalBase.Application.Notificacoes;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// Consome a fila e envia cada e-mail com NOVA TENTATIVA controlada (regra 11): até
/// <see cref="OpcoesSmtp.TentativasMaximas"/> tentativas, espera exponencial (2s, 4s, 8s...), cancelável. Falha final
/// vai para o log SEM destinatário nem conteúdo (regra 8) — só um identificador da tentativa e o tipo do erro.
/// Hospedado pela Web (BackgroundService), testável sem host.
/// FILA-001 (auditoria de 08/10/2026): no DESLIGAMENTO (reciclagem do pool, parada do site) o que está na fila — e a
/// mensagem que estava sendo enviada — ganha uma tentativa dentro de <see cref="PrazoDesligamentoPadrao"/>; o que não
/// sair é contado e vai para o log como erro (Visualizador de Eventos), em vez de sumir em silêncio. O conteúdo
/// continua só em memória (link de uso único: nunca em banco ou log — regras 7/8).
/// </summary>
public sealed partial class ProcessadorFilaEmail
{
    private readonly FilaEmailEmMemoria _fila;
    private readonly IServiceScopeFactory _escopos;
    private readonly IOptions<OpcoesEmail> _opcoes;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;
    private readonly TimeSpan _prazoDesligamento;
    private readonly ILogger<ProcessadorFilaEmail> _logger;

    public ProcessadorFilaEmail(
        FilaEmailEmMemoria fila,
        IServiceScopeFactory escopos,
        IOptions<OpcoesEmail> opcoes,
        ILogger<ProcessadorFilaEmail> logger,
        Func<TimeSpan, CancellationToken, Task>? esperar = null,
        TimeSpan? prazoDesligamento = null)
    {
        ArgumentNullException.ThrowIfNull(fila);
        ArgumentNullException.ThrowIfNull(escopos);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(logger);
        _fila = fila;
        _escopos = escopos;
        _opcoes = opcoes;
        _esperar = esperar ?? Task.Delay;
        _prazoDesligamento = prazoDesligamento is { } prazo && prazo > TimeSpan.Zero ? prazo : PrazoDesligamentoPadrao;
        _logger = logger;
    }

    /// <summary>Tempo para esvaziar a fila no desligamento (bem abaixo do prazo de parada do host e do IIS).</summary>
    public static readonly TimeSpan PrazoDesligamentoPadrao = TimeSpan.FromSeconds(10);

    /// <summary>Processa até o cancelamento (desligamento do host) e então esvazia a fila dentro do prazo.</summary>
    public async Task ProcessarAsync(CancellationToken cancellationToken)
    {
        MensagemEmail? emAndamento = null;
        try
        {
            await foreach (var mensagem in _fila.Leitor.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                emAndamento = mensagem;
                await EnviarComNovaTentativaAsync(mensagem, cancellationToken).ConfigureAwait(false);
                emAndamento = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // desligamento do host: segue para o esvaziamento
        }

        await EsvaziarNoDesligamentoAsync(emAndamento).ConfigureAwait(false);
    }

    /// <summary>
    /// Uma tentativa para cada e-mail ainda na fila (e para o que foi interrompido), dentro do prazo de desligamento.
    /// O que não sair é contado como não enviado e registrado como erro — nunca o destinatário nem o conteúdo.
    /// </summary>
    internal async Task EsvaziarNoDesligamentoAsync(MensagemEmail? interrompida)
    {
        var pendentes = new List<MensagemEmail>();
        if (interrompida is not null)
        {
            pendentes.Add(interrompida);
        }

        while (_fila.Leitor.TryRead(out var mensagem))
        {
            pendentes.Add(mensagem);
        }

        if (pendentes.Count == 0)
        {
            return;
        }

        LogEsvaziando(_logger, pendentes.Count, (int)_prazoDesligamento.TotalSeconds);
        using var prazo = new CancellationTokenSource(_prazoDesligamento);
        var enviados = 0;
        var perdidos = 0;
        foreach (var mensagem in pendentes)
        {
            if (prazo.IsCancellationRequested)
            {
                perdidos++;
                continue;
            }

            try
            {
                // A falha do transporte já é contada e registrada dentro do envio (falha final).
                if (await EnviarComNovaTentativaAsync(mensagem, prazo.Token, tentativasMaximas: 1).ConfigureAwait(false))
                {
                    enviados++;
                }
            }
            catch (OperationCanceledException)
            {
                perdidos++;
            }
        }

        if (perdidos > 0)
        {
            _fila.RegistrarNaoEnviados(perdidos);
            LogPerdidosNoDesligamento(_logger, perdidos, (int)_prazoDesligamento.TotalSeconds);
        }

        LogEsvaziado(_logger, enviados, pendentes.Count);
    }

    internal async Task<bool> EnviarComNovaTentativaAsync(MensagemEmail mensagem, CancellationToken cancellationToken, int? tentativasMaximas = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        // Configuração vigente no início deste envio (pode ter mudado pela tela).
        var vigente = _opcoes.Value;
        var tentativas = vigente.Modo == OpcoesEmail.ModoSmtp ? Math.Clamp(vigente.Smtp.TentativasMaximas, 1, 10) : 1;
        if (tentativasMaximas is { } maximo)
        {
            tentativas = Math.Clamp(maximo, 1, tentativas);
        }
        for (var tentativa = 1; tentativa <= tentativas; tentativa++)
        {
            try
            {
                await using var escopo = _escopos.CreateAsyncScope();
                await escopo.ServiceProvider.GetRequiredService<IEnviadorEmail>().EnviarAsync(mensagem, cancellationToken).ConfigureAwait(false);
                LogEnviado(_logger, id, tentativa);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Qualquer falha do transporte é tratada aqui: nova tentativa ou registro final (o worker não pode morrer).
            catch (Exception excecao)
#pragma warning restore CA1031
            {
                // Só o TIPO do erro: a mensagem de exceções de SMTP pode citar o destinatário.
                if (tentativa == tentativas)
                {
                    _fila.RegistrarNaoEnviados();
                    LogFalhaFinal(_logger, id, tentativa, excecao.GetType().Name);
                    return false;
                }

                LogFalhaTentativa(_logger, id, tentativa, excecao.GetType().Name);
                await _esperar(TimeSpan.FromSeconds(Math.Pow(2, tentativa)), cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    [LoggerMessage(EventId = 7110, Level = LogLevel.Information, Message = "E-mail {Id} enviado (tentativa {Tentativa}).")]
    private static partial void LogEnviado(ILogger logger, string id, int tentativa);

    [LoggerMessage(EventId = 7111, Level = LogLevel.Warning, Message = "E-mail {Id}: falha na tentativa {Tentativa} ({TipoErro}); tentando de novo.")]
    private static partial void LogFalhaTentativa(ILogger logger, string id, int tentativa, string tipoErro);

    [LoggerMessage(EventId = 7113, Level = LogLevel.Information, Message = "Desligamento: {Pendentes} e-mail(s) na fila; tentando enviar em até {Segundos} s.")]
    private static partial void LogEsvaziando(ILogger logger, int pendentes, int segundos);

    [LoggerMessage(EventId = 7114, Level = LogLevel.Information, Message = "Desligamento: {Enviados} de {Pendentes} e-mail(s) da fila enviados.")]
    private static partial void LogEsvaziado(ILogger logger, int enviados, int pendentes);

    [LoggerMessage(EventId = 7115, Level = LogLevel.Error, Message = "Desligamento: {Perdidos} e-mail(s) da fila NÃO enviados no prazo de {Segundos} s (o conteúdo fica só na memória e se perde). Quem pediu (ex.: redefinição de senha) precisa pedir de novo.")]
    private static partial void LogPerdidosNoDesligamento(ILogger logger, int perdidos, int segundos);

    [LoggerMessage(EventId = 7112, Level = LogLevel.Error, Message = "E-mail {Id} NÃO enviado após {Tentativa} tentativa(s) ({TipoErro}). Verifique Configurações → E-mail (ou Email:Smtp) e o servidor de e-mail.")]
    private static partial void LogFalhaFinal(ILogger logger, string id, int tentativa, string tipoErro);
}
