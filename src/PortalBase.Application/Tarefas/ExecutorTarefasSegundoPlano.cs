using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Tarefas;

namespace PortalBase.Application.Tarefas;

/// <summary>
/// EXECUTA as tarefas em segundo plano (ARQ-006), uma por vez neste processo: (1) reivindica no catálogo a mais antiga
/// pendente (ou cujo prazo venceu — o processo anterior caiu), com concorrência otimista: dois executores nunca rodam a
/// mesma; (2) num escopo próprio, define a identidade DE QUEM PEDIU e resolve o contribuinte no servidor
/// (<see cref="IResolvedorTenant"/> revalida o acesso — acesso revogado no meio do caminho ⇒ a tarefa falha); (3) roda o
/// manipulador do tipo, renovando o prazo enquanto ele trabalha; (4) grava o resultado (ou o motivo sanitizado da falha)
/// e avisa quem espera. No desligamento a tarefa fica "em execução": o prazo vence e ela é retomada.
/// </summary>
public sealed partial class ExecutorTarefasSegundoPlano
{
    /// <summary>Nome do job na auditoria.</summary>
    public const string NomeJob = "tarefas";

    private const int CandidatasPorBusca = 5;

    private readonly IServiceScopeFactory _escopos;
    private readonly IDefinidorIdentidadeTarefa _identidade;
    private readonly SinalTarefas _sinal;
    private readonly IRelogio _relogio;
    private readonly OpcoesTarefas _opcoes;
    private readonly ILogger<ExecutorTarefasSegundoPlano> _logger;
    private readonly string _executor = $"{Environment.MachineName}:{Environment.ProcessId}";

    public ExecutorTarefasSegundoPlano(
        IServiceScopeFactory escopos, IDefinidorIdentidadeTarefa identidade, SinalTarefas sinal, IRelogio relogio, OpcoesTarefas opcoes,
        ILogger<ExecutorTarefasSegundoPlano> logger)
    {
        ArgumentNullException.ThrowIfNull(escopos);
        ArgumentNullException.ThrowIfNull(identidade);
        ArgumentNullException.ThrowIfNull(sinal);
        ArgumentNullException.ThrowIfNull(relogio);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(logger);
        _escopos = escopos;
        _identidade = identidade;
        _sinal = sinal;
        _relogio = relogio;
        _opcoes = opcoes;
        _logger = logger;
    }

    /// <summary>Reivindica e executa UMA tarefa. <c>false</c> quando não havia nenhuma para executar.</summary>
    public async Task<bool> ExecutarProximaAsync(CancellationToken cancellationToken)
    {
        var tarefa = await ReivindicarAsync(cancellationToken).ConfigureAwait(false);
        if (tarefa is null)
        {
            return false;
        }

        string? resultado = null;
        string? motivo = null;
        using (var renovacao = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var renovando = RenovarEnquantoRodaAsync(tarefa.Id, renovacao.Token);
            try
            {
                resultado = await ExecutarNoEscopoAsync(tarefa, cancellationToken).ConfigureAwait(false);
                LogConcluida(_logger, tarefa.Id, tarefa.Tipo);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Desligamento: fica "em execução"; o prazo vence e a tarefa é retomada por este ou outro processo.
                LogInterrompida(_logger, tarefa.Id, tarefa.Tipo);
                throw;
            }
            catch (Exception ex)
            {
                motivo = MotivoSeguro(ex);
                LogFalhou(_logger, tarefa.Id, tarefa.Tipo, ex.GetType().Name);
            }
            finally
            {
                await renovacao.CancelAsync().ConfigureAwait(false);
                await renovando.ConfigureAwait(false);
            }
        }

        await TerminarAsync(tarefa.Id, resultado, motivo).ConfigureAwait(false);
        _sinal.Concluida(tarefa.Id);
        return true;
    }

    private async Task<string> ExecutarNoEscopoAsync(TarefaSegundoPlano tarefa, CancellationToken cancellationToken)
    {
        await using var escopo = _escopos.CreateAsyncScope();
        var provedor = escopo.ServiceProvider;

        // Identidade de QUEM PEDIU (as permissões dele são conferidas de novo pelo caso de uso) e contribuinte resolvido
        // no servidor a partir dela — o mesmo caminho de uma requisição (regras 3, 4, 5).
        _identidade.Definir(provedor, tarefa.SolicitanteId);
        var auditoria = provedor.GetRequiredService<IContextoAuditoriaMutavel>();
        auditoria.DefinirOrigem("Servidor", TipoAtor.JobBackground, NomeJob);
        auditoria.DefinirCorrelacao(tarefa.Id.ToString("N"), null);
        await provedor.GetRequiredService<IResolvedorTenant>()
            .ResolverAsync(tarefa.SolicitanteId.ToString(), tarefa.TenantId, cancellationToken).ConfigureAwait(false);

        var manipulador = provedor.GetServices<IManipuladorTarefa>().FirstOrDefault(m => m.Tipo == tarefa.Tipo)
            ?? throw new InvalidOperationException($"Sem manipulador para a tarefa {tarefa.Tipo}.");
        return await manipulador.ExecutarAsync(tarefa.Parametros, retomada: tarefa.Tentativas > 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reivindica a próxima tarefa (cópia desanexada) ou nulo. Tarefas além do limite de tentativas falham aqui.</summary>
    private async Task<TarefaSegundoPlano?> ReivindicarAsync(CancellationToken cancellationToken)
    {
        await using var escopo = _escopos.CreateAsyncScope();
        var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioTarefas>();
        var agora = _relogio.AgoraUtc;
        foreach (var candidata in await repositorio.ListarReivindicaveisAsync(agora, CandidatasPorBusca, cancellationToken).ConfigureAwait(false))
        {
            var reivindicou = candidata.Reivindicar(agora, _opcoes.PrazoReivindicacao, _executor);
            if (!await repositorio.TentarSalvarAsync(cancellationToken).ConfigureAwait(false))
            {
                continue; // outro executor chegou antes
            }

            if (reivindicou)
            {
                if (candidata.Tentativas > 1)
                {
                    LogRetomada(_logger, candidata.Id, candidata.Tipo, candidata.Tentativas);
                }

                return candidata;
            }

            LogDesistiu(_logger, candidata.Id, candidata.Tipo, candidata.Tentativas);
            _sinal.Concluida(candidata.Id);
        }

        return null;
    }

    private async Task RenovarEnquantoRodaAsync(Guid tarefaId, CancellationToken cancellationToken)
    {
        var intervalo = _opcoes.PrazoReivindicacao / 2;
        try
        {
            while (true)
            {
                await Task.Delay(intervalo, cancellationToken).ConfigureAwait(false);
                await using var escopo = _escopos.CreateAsyncScope();
                var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioTarefas>();
                var tarefa = await repositorio.ObterPorIdAsync(tarefaId, cancellationToken).ConfigureAwait(false);
                if (tarefa is not { Estado: EstadoTarefaSegundoPlano.EmExecucao })
                {
                    return;
                }

                tarefa.Renovar(_relogio.AgoraUtc, _opcoes.PrazoReivindicacao);
                await repositorio.TentarSalvarAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // fim da execução
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sem renovar, o prazo pode vencer e a tarefa ser retomada por outro executor: só registra.
            LogFalhaRenovar(_logger, tarefaId, ex.GetType().Name);
        }
    }

    private async Task TerminarAsync(Guid tarefaId, string? resultado, string? motivo)
    {
        for (var tentativa = 1; tentativa <= 2; tentativa++)
        {
            await using var escopo = _escopos.CreateAsyncScope();
            var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioTarefas>();
            var tarefa = await repositorio.ObterPorIdAsync(tarefaId, CancellationToken.None).ConfigureAwait(false);
            if (tarefa is not { Estado: EstadoTarefaSegundoPlano.EmExecucao })
            {
                return;
            }

            if (motivo is null)
            {
                tarefa.Concluir(resultado, _relogio.AgoraUtc);
            }
            else
            {
                tarefa.Falhar(motivo, _relogio.AgoraUtc);
            }

            if (await repositorio.TentarSalvarAsync(CancellationToken.None).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Motivo para a tela: mensagem de regra (já pensada para o usuário) ou genérica — nunca detalhe interno.</summary>
    public static string MotivoSeguro(Exception ex) => ex switch
    {
        TenantNaoAutorizadoException or TenantContextAusenteException or TenantNaoEncontradoException
            => "Quem pediu não tem mais acesso a este contribuinte.",
        AutorizacaoNegadaException or NaoAutenticadoException or MfaRequeridoException => "Quem pediu não tem mais permissão para esta operação.",
        DominioException dominio => dominio.Message,
        _ => "Falha inesperada ao executar a tarefa. Tente de novo; se repetir, chame o suporte informando o horário.",
    };

    [LoggerMessage(EventId = 7500, Level = LogLevel.Information, Message = "Tarefa {TarefaId} ({Tipo}) concluída.")]
    private static partial void LogConcluida(ILogger logger, Guid tarefaId, TipoTarefaSegundoPlano tipo);

    [LoggerMessage(EventId = 7501, Level = LogLevel.Error, Message = "Tarefa {TarefaId} ({Tipo}) falhou ({TipoErro}).")]
    private static partial void LogFalhou(ILogger logger, Guid tarefaId, TipoTarefaSegundoPlano tipo, string tipoErro);

    [LoggerMessage(EventId = 7502, Level = LogLevel.Warning, Message = "Tarefa {TarefaId} ({Tipo}) interrompida pelo desligamento; será retomada quando o prazo vencer.")]
    private static partial void LogInterrompida(ILogger logger, Guid tarefaId, TipoTarefaSegundoPlano tipo);

    [LoggerMessage(EventId = 7503, Level = LogLevel.Warning, Message = "Tarefa {TarefaId} ({Tipo}) retomada (tentativa {Tentativa}): a execução anterior foi interrompida.")]
    private static partial void LogRetomada(ILogger logger, Guid tarefaId, TipoTarefaSegundoPlano tipo, int tentativa);

    [LoggerMessage(EventId = 7504, Level = LogLevel.Error, Message = "Tarefa {TarefaId} ({Tipo}) desistida após {Tentativas} execuções interrompidas.")]
    private static partial void LogDesistiu(ILogger logger, Guid tarefaId, TipoTarefaSegundoPlano tipo, int tentativas);

    [LoggerMessage(EventId = 7505, Level = LogLevel.Warning, Message = "Tarefa {TarefaId}: não foi possível renovar o prazo ({TipoErro}).")]
    private static partial void LogFalhaRenovar(ILogger logger, Guid tarefaId, string tipoErro);
}
