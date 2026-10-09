namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// View-models do FRONTEND ADMINISTRAÇÃO E OPERAÇÕES (Prompt 40). Projeções de UI (strings) — nunca tipos
// de domínio/EF na tela. Incidentes e Operações são consultas de PLATAFORMA (cross-tenant, autorizadas por
// seguranca.incidentes.gerir). Sem dados fictícios: quando o backend não fornece, a página mostra estado
// honesto ("indisponível nesta etapa"). NUNCA expõe segredo, token, certificado, evidência protegida.
// =====================================================================================================

// ---- Incidentes -------------------------------------------------------------------------------------

public sealed record FiltroIncidentesVm : FiltroPaginadoVm
{
    /// <summary>Período inclusivo sobre DetectadoEmUtc (Prompt 81) — validado de novo no servidor.</summary>
    public DateTimeOffset? Desde { get; init; }
    public DateTimeOffset? Ate { get; init; }
    public string? Tipo { get; init; }
    public string? Severidade { get; init; }
    public string? Status { get; init; }
    public Guid? TenantAfetadoId { get; init; }
    public string? Codigo { get; init; }
    public string Ordenacao { get; init; } = "Detectado";
    public bool Descendente { get; init; } = true;
}

public sealed record RegistroIncidenteVm(
    Guid Id, string Codigo, string Tipo, string Severidade, string SeveridadeBadge, string Status, string StatusBadge,
    string Titulo, Guid? TenantAfetadoId, string ClassificacaoRisco, string ClassificacaoRiscoBadge,
    string? ResponsavelId, DateTimeOffset DetectadoEmUtc, DateTimeOffset AtualizadoEmUtc, DateTimeOffset? EncerradoEmUtc)
{
    /// <summary>Incidente ainda em tratamento (não encerrado).</summary>
    public bool EmAberto => Status is not "Encerrado";
}

// ---- Operações (observabilidade — snapshot) ---------------------------------------------------------

public sealed record RegistroWorkerVm(
    string Worker, bool Ativo, bool Estagnado, string EstadoRotulo, string EstadoBadge,
    DateTimeOffset? IniciadoEmUtc, DateTimeOffset? UltimaBatidaUtc, DateTimeOffset? UltimoSucessoUtc,
    long TotalProcessado, DateTimeOffset? UltimoErroUtc, string? UltimoErroCodigo, long TotalErros, int Reinicios);

public sealed record RegistroIntegracaoOpVm(
    string Integracao, bool Degradada, string EstadoRotulo, string EstadoBadge,
    DateTimeOffset? UltimoSucessoUtc, DateTimeOffset? UltimaFalhaUtc, string? UltimoErroCodigo,
    long TotalSucesso, long TotalFalha);

public sealed record PainelOperacoesVm(
    IReadOnlyList<RegistroWorkerVm> Workers, IReadOnlyList<RegistroIntegracaoOpVm> Integracoes,
    int WorkersEstagnados, int IntegracoesDegradadas, DateTimeOffset GeradoEmUtc)
{
    /// <summary>
    /// Nenhum worker registrou saúde (nem no processo da Web nem no catálogo, onde o host Worker grava a cada
    /// batida). NÃO é "saudável": é ausência de informação (§8 do Prompt 40).
    /// </summary>
    public bool WorkersSemTelemetria => Workers.Count == 0;

    public bool IntegracoesSemTelemetria => Integracoes.Count == 0;

    /// <summary>Situação do serviço do Worker (batida no catálogo — OBS-003); nulo se não avaliada.</summary>
    public ServicoWorkerVm? ServicoWorker { get; init; }
}

/// <summary>Serviço do Worker para a tela: rótulo, cor, mensagem e se está fora do ar.</summary>
public sealed record ServicoWorkerVm(string Rotulo, string Badge, string Mensagem, bool ForaDoAr)
{
    public static ServicoWorkerVm De(PortalBase.Application.Observabilidade.SituacaoServicoWorker situacao)
    {
        ArgumentNullException.ThrowIfNull(situacao);
        var (rotulo, badge) = situacao.Estado switch
        {
            PortalBase.Application.Observabilidade.EstadoServicoWorker.EmExecucao => ("Em execução", "ok"),
            PortalBase.Application.Observabilidade.EstadoServicoWorker.NaoEsperado => ("Não instalado", "neutral"),
            PortalBase.Application.Observabilidade.EstadoServicoWorker.Degradado => ("Com problema", "warn"),
            PortalBase.Application.Observabilidade.EstadoServicoWorker.Parado => ("Parado", "bad"),
            PortalBase.Application.Observabilidade.EstadoServicoWorker.SemSinal => ("Sem sinal", "bad"),
            _ => ("Nunca rodou", "bad"),
        };
        return new ServicoWorkerVm(rotulo, badge, situacao.Mensagem, situacao.ServicoForaDoAr);
    }
}
