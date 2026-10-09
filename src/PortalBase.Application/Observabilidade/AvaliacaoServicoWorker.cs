using System.Globalization;

namespace PortalBase.Application.Observabilidade;

/// <summary>Estado do SERVIÇO do Worker (o processo que faz backup e as rotinas em segundo plano), visto pelo catálogo.</summary>
public enum EstadoServicoWorker
{
    /// <summary>Servidor sem Worker por decisão de instalação (<see cref="OpcoesObservabilidade.WorkerEsperado"/> = false).</summary>
    NaoEsperado = 0,

    /// <summary>Batendo dentro do limite e sem trabalhador travado ou falhando.</summary>
    EmExecucao = 1,

    /// <summary>O serviço bate, mas algum trabalhador está travado ou com a última execução em falha.</summary>
    Degradado = 2,

    /// <summary>Nenhuma batida gravada no catálogo: o Worker nunca rodou contra este banco.</summary>
    NuncaRegistrado = 3,

    /// <summary>O serviço foi encerrado (desligamento gravado) e não voltou.</summary>
    Parado = 4,

    /// <summary>Sem batida além do limite: o processo caiu ou travou sem conseguir gravar o encerramento.</summary>
    SemSinal = 5,
}

/// <summary>Resultado da avaliação: estado, mensagem para o operador e os trabalhadores com problema.</summary>
public sealed record SituacaoServicoWorker(
    EstadoServicoWorker Estado,
    string Mensagem,
    DateTimeOffset? UltimaBatidaUtc,
    IReadOnlyList<string> Travados,
    IReadOnlyList<string> ComFalha)
{
    /// <summary>O serviço não está rodando (nunca rodou, parou ou sumiu): nada do que ele faz está acontecendo.</summary>
    public bool ServicoForaDoAr => Estado is EstadoServicoWorker.NuncaRegistrado or EstadoServicoWorker.Parado or EstadoServicoWorker.SemSinal;
}

/// <summary>
/// Avaliação ÚNICA do serviço do Worker a partir da saúde que ele grava no CATÁLOGO a cada batida — OBS-003 (auditoria
/// de 08/10/2026): o health da Web lia só o registro em memória do próprio processo da Web (onde o Worker não roda) e
/// respondia sempre "saudável"; um Worker parado não aparecia em lugar nenhum. Usada pelo health check, pelo painel de
/// Operações e pelos alertas (mesma regra nos três).
/// </summary>
public static class AvaliacaoServicoWorker
{
    /// <summary>Nome do trabalhador de batida do host do Worker (grava o snapshot no catálogo a cada intervalo).</summary>
    public const string WorkerBatida = "heartbeat";

    public static SituacaoServicoWorker Avaliar(IReadOnlyList<EstadoSaudeWorker> persistidos, DateTimeOffset agoraUtc, OpcoesObservabilidade opcoes)
    {
        ArgumentNullException.ThrowIfNull(persistidos);
        ArgumentNullException.ThrowIfNull(opcoes);

        var batida = persistidos.FirstOrDefault(e => string.Equals(e.Worker, WorkerBatida, StringComparison.Ordinal));
        if (!opcoes.WorkerEsperado)
        {
            return new SituacaoServicoWorker(EstadoServicoWorker.NaoEsperado,
                "Servidor configurado sem o serviço do Worker (Observabilidade:WorkerEsperado = false).", batida?.UltimaBatidaUtc, [], []);
        }

        if (batida?.UltimaBatidaUtc is not { } ultima)
        {
            return new SituacaoServicoWorker(EstadoServicoWorker.NuncaRegistrado,
                "O serviço do Worker nunca registrou batida neste catálogo: confira se o serviço PortalBase.Worker está instalado e iniciado.",
                null, [], []);
        }

        var minutos = Minutos(agoraUtc - ultima);
        if (!batida.Ativo)
        {
            return new SituacaoServicoWorker(EstadoServicoWorker.Parado,
                $"O serviço do Worker está parado (encerrou há {minutos} min): backup automático e as rotinas do Worker não estão rodando.",
                ultima, [], []);
        }

        if (agoraUtc - ultima > opcoes.LimiteEstagnacao)
        {
            return new SituacaoServicoWorker(EstadoServicoWorker.SemSinal,
                $"O serviço do Worker não dá sinal há {minutos} min (caiu ou travou): backup automático e as rotinas do Worker não estão rodando.",
                ultima, [], []);
        }

        var outros = persistidos.Where(e => !string.Equals(e.Worker, WorkerBatida, StringComparison.Ordinal)).ToList();
        var travados = outros.Where(e => e.Estagnado(opcoes.LimiteEstagnacao, agoraUtc)).Select(e => e.Worker).Order(StringComparer.Ordinal).ToList();
        var comFalha = outros.Where(EmFalha).Select(e => e.Worker).Order(StringComparer.Ordinal).ToList();
        if (travados.Count > 0 || comFalha.Count > 0)
        {
            var partes = new List<string>();
            if (travados.Count > 0)
            {
                partes.Add("travados: " + string.Join(", ", travados));
            }

            if (comFalha.Count > 0)
            {
                partes.Add("com a última execução em falha: " + string.Join(", ", comFalha));
            }

            return new SituacaoServicoWorker(EstadoServicoWorker.Degradado,
                $"O serviço do Worker está rodando, mas há trabalhadores {string.Join("; ", partes)}. Veja Operações → Workers.",
                ultima, travados, comFalha);
        }

        return new SituacaoServicoWorker(EstadoServicoWorker.EmExecucao,
            string.Create(CultureInfo.InvariantCulture, $"Serviço do Worker em execução (última batida há {(int)(agoraUtc - ultima).TotalSeconds} s)."),
            ultima, [], []);
    }

    /// <summary>A última coisa registrada pelo trabalhador foi um erro (depois do último sucesso)?</summary>
    public static bool EmFalha(EstadoSaudeWorker estado)
    {
        ArgumentNullException.ThrowIfNull(estado);
        return estado.UltimoErroUtc is { } erro && (estado.UltimoSucessoUtc is not { } sucesso || erro > sucesso);
    }

    private static string Minutos(TimeSpan intervalo)
        => Math.Max(0, (int)intervalo.TotalMinutes).ToString(CultureInfo.InvariantCulture);
}
