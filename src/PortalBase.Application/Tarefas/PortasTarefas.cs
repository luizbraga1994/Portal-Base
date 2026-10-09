using System.Collections.Concurrent;
using PortalBase.Domain.Tarefas;

namespace PortalBase.Application.Tarefas;

// ============================================================================
// TAREFAS EM SEGUNDO PLANO (ARQ-006, auditoria de 08/10/2026) — o processamento pesado rodava no
// circuito do Blazor: fechar a aba, perder a conexão ou reciclar o pool interrompia a operação no meio. A tela agora
// enfileira (FilaTarefasSegundoPlano), um executor no servidor roda (ExecutorTarefasSegundoPlano) e o estado fica no
// catálogo — consultável e retomado se o processo cair.
// ============================================================================

/// <summary>Repositório das tarefas, no catálogo (uma fila só para todos os contribuintes; cada tarefa diz o seu).</summary>
public interface IRepositorioTarefas
{
    Task AdicionarAsync(TarefaSegundoPlano tarefa, CancellationToken cancellationToken = default);

    /// <summary>A tarefa RASTREADA (para mudar o estado e gravar).</summary>
    Task<TarefaSegundoPlano?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Leitura sem rastreamento (sempre o estado atual do banco) — para acompanhar.</summary>
    Task<TarefaSegundoPlano?> LerAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>A tarefa ainda ativa (pendente ou em execução) do mesmo pedido, se houver.</summary>
    Task<TarefaSegundoPlano?> ObterAtivaAsync(Guid tenantId, TipoTarefaSegundoPlano tipo, string chaveIdempotencia, CancellationToken cancellationToken = default);

    /// <summary>Candidatas a executar agora (pendentes e em execução com prazo vencido), das mais antigas, rastreadas.</summary>
    Task<IReadOnlyList<TarefaSegundoPlano>> ListarReivindicaveisAsync(DateTimeOffset agoraUtc, int limite, CancellationToken cancellationToken = default);

    Task SalvarAsync(CancellationToken cancellationToken = default);

    /// <summary>Grava; <c>false</c> se outra execução mudou a tarefa antes (concorrência otimista).</summary>
    Task<bool> TentarSalvarAsync(CancellationToken cancellationToken = default);
}

/// <summary>O que executa um tipo de tarefa, no escopo já com a identidade do solicitante e o contribuinte resolvidos.</summary>
public interface IManipuladorTarefa
{
    TipoTarefaSegundoPlano Tipo { get; }

    /// <param name="parametros">JSON gravado no pedido.</param>
    /// <param name="retomada">A tentativa anterior foi interrompida (o processo caiu): o que ela deixou "em andamento" pode ser retomado.</param>
    Task<string> ExecutarAsync(string parametros, bool retomada, CancellationToken cancellationToken);
}

/// <summary>Define, no escopo da execução, a identidade de quem pediu a tarefa (cada host tem o seu contexto de segurança).</summary>
public interface IDefinidorIdentidadeTarefa
{
    void Definir(IServiceProvider escopo, Guid solicitanteId);
}

/// <summary>Situação de uma tarefa para a tela (sem nada além do que o próprio contribuinte pediu).</summary>
public sealed record SituacaoTarefa(
    Guid Id, TipoTarefaSegundoPlano Tipo, EstadoTarefaSegundoPlano Estado, string? Resultado, string? Motivo,
    DateTimeOffset CriadaEmUtc, DateTimeOffset? ConcluidaEmUtc)
{
    public bool Ativa => Estado is EstadoTarefaSegundoPlano.Pendente or EstadoTarefaSegundoPlano.EmExecucao;

    public static SituacaoTarefa De(TarefaSegundoPlano t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new SituacaoTarefa(t.Id, t.Tipo, t.Estado, t.Resultado, t.Motivo, t.CriadaEmUtc, t.ConcluidaEmUtc);
    }
}

/// <summary>Configuração das tarefas (seção <c>Tarefas</c>). Sem segredo.</summary>
public sealed class OpcoesTarefas
{
    public const string Secao = "Tarefas";

    /// <summary>Este processo executa as tarefas (a Web, por padrão).</summary>
    public bool ExecutorHabilitado { get; set; } = true;

    /// <summary>Quanto tempo a tela espera o resultado antes de dizer "em andamento" (segundos, 1 a 120).</summary>
    public int EsperaTelaSegundos { get; set; } = 20;

    /// <summary>Sem aviso deste processo, de quanto em quanto tempo o executor procura tarefas no catálogo (segundos).</summary>
    public int IntervaloBuscaSegundos { get; set; } = 5;

    /// <summary>Prazo de cada reivindicação (minutos); renovado na metade enquanto a tarefa roda.</summary>
    public int PrazoReivindicacaoMinutos { get; set; } = 4;

    /// <summary>Quantas tarefas este processo executa ao mesmo tempo (1 a 8): uma importação longa não trava as outras.</summary>
    public int ExecucoesSimultaneas { get; set; } = 2;

    public TimeSpan EsperaTela => TimeSpan.FromSeconds(Math.Clamp(EsperaTelaSegundos, 1, 120));

    public TimeSpan IntervaloBusca => TimeSpan.FromSeconds(Math.Clamp(IntervaloBuscaSegundos, 1, 60));

    public TimeSpan PrazoReivindicacao => TimeSpan.FromMinutes(Math.Clamp(PrazoReivindicacaoMinutos, 1, 60));

    public int Simultaneas => Math.Clamp(ExecucoesSimultaneas, 1, 8);
}

/// <summary>
/// Sinais DENTRO do processo (singleton): acorda o executor quando a tela enfileira e avisa quem espera quando a
/// tarefa termina — sem ficar consultando o banco à toa. Entre processos, vale a busca periódica.
/// </summary>
public sealed class SinalTarefas : IDisposable
{
    private readonly SemaphoreSlim _novas = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _concluidas = new();

    public void NovaTarefa() => _novas.Release();

    public void Dispose() => _novas.Dispose();

    /// <summary>Espera uma tarefa nova ou o intervalo (o que vier primeiro).</summary>
    public async Task AguardarNovaAsync(TimeSpan intervalo, CancellationToken cancellationToken)
    {
        await _novas.WaitAsync(intervalo, cancellationToken).ConfigureAwait(false);
    }

    public void Concluida(Guid tarefaId)
    {
        if (_concluidas.TryRemove(tarefaId, out var aviso))
        {
            aviso.TrySetResult();
        }
    }

    /// <summary>Espera o aviso de conclusão (deste processo) ou o tempo; quem chama confere o estado no banco.</summary>
    public async Task AguardarConclusaoAsync(Guid tarefaId, TimeSpan tempo, CancellationToken cancellationToken)
    {
        var aviso = _concluidas.GetOrAdd(tarefaId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await aviso.Task.WaitAsync(tempo, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // segue para conferir no banco
        }
        finally
        {
            // Não acumula avisos de tarefas executadas em OUTRO processo (só remove o próprio aviso).
            _concluidas.TryRemove(new KeyValuePair<Guid, TaskCompletionSource>(tarefaId, aviso));
        }
    }
}
