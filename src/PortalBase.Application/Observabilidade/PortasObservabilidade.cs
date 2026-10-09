namespace PortalBase.Application.Observabilidade;

// ============================================================================
// OBSERVABILIDADE / OPERAÇÕES (Prompt 31) — abstrações in-memory de baixo custo para saúde de workers, última
// integração e ponte alerta→incidente. NUNCA carregam PII/payload/segredo (§3/§80): só nome técnico do worker,
// contadores, timestamps, códigos de erro e correlação. Sem cardinalidade abusiva (§9/§10).
// ============================================================================

/// <summary>Configuração operacional (Prompt 31, seções 21, 37). Sem segredo. Limiares configuráveis (§21).</summary>
public sealed class OpcoesObservabilidade
{
    public const string Secao = "Observabilidade";

    /// <summary>Sem batida por mais que isto ⇒ worker possivelmente ESTAGNADO (§21). Configurável, não arbitrário.</summary>
    public int LimiteEstagnacaoSegundos { get; set; } = 300;

    /// <summary>Intervalo do heartbeat do worker (Prompt 67). Menor que <see cref="LimiteEstagnacaoSegundos"/>.</summary>
    public int HeartbeatIntervaloSegundos { get; set; } = 30;

    public TimeSpan HeartbeatIntervalo => TimeSpan.FromSeconds(Math.Clamp(HeartbeatIntervaloSegundos, 5, 300));

    /// <summary>Sem integração externa bem-sucedida por mais que isto ⇒ saúde de integração DEGRADADA (§16/§44).</summary>
    public int LimiteIntegracaoStaleSegundos { get; set; } = 3600;


    /// <summary>
    /// Este servidor tem o serviço do Worker (padrão). Falso só quando a instalação é sem Worker
    /// (<c>configurar-iis.ps1 -SemWorker</c>): aí a ausência de batida não é problema.
    /// </summary>
    public bool WorkerEsperado { get; set; } = true;

    /// <summary>Severidade mínima de alerta que ABRE um incidente técnico automaticamente (§50).</summary>
    public SeveridadeAlerta SeveridadeMinimaIncidente { get; set; } = SeveridadeAlerta.Alto;

    /// <summary>Rotina de alertas ligada (OBS-004). Desligar só em teste ou com outra ferramenta de monitoração.</summary>
    public bool AlertasHabilitados { get; set; } = true;

    /// <summary>
    /// Quem recebe os alertas por e-mail, separados por ponto e vírgula (ex.: <c>ti@empresa.com.br;dpo@empresa.com.br</c>).
    /// Vazio ⇒ só Visualizador de Eventos, log e incidente. Endereço não é segredo, mas nunca vai para o log.
    /// </summary>
    public string EmailsAlerta { get; set; } = string.Empty;

    /// <summary>De quanto em quanto tempo a rotina de alertas confere tudo. Minutos (1 a 60).</summary>
    public int IntervaloAlertasMinutos { get; set; } = 5;

    /// <summary>Enquanto o problema persistir, o alerta é repetido a cada tantas horas (1 a 168).</summary>
    public int RenotificarAlertaHoras { get; set; } = 24;

    /// <summary>Sem backup do catálogo concluído há mais que isto (com o backup agendado ligado) ⇒ alerta crítico. Horas.</summary>
    public int AlertaBackupHoras { get; set; } = 26;

    /// <summary>Disco com menos que este percentual livre ⇒ alerta (metade disso ⇒ crítico).</summary>
    public int DiscoLivreMinimoPercentual { get; set; } = 10;

    /// <summary>Disco com menos que estes GB livres ⇒ alerta (metade disso ⇒ crítico).</summary>
    public int DiscoLivreMinimoGb { get; set; } = 5;

    public TimeSpan IntervaloAlertas => TimeSpan.FromMinutes(Math.Clamp(IntervaloAlertasMinutos, 1, 60));

    public TimeSpan RenotificarAlerta => TimeSpan.FromHours(Math.Clamp(RenotificarAlertaHoras, 1, 168));

    public TimeSpan LimiteEstagnacao => TimeSpan.FromSeconds(Math.Max(30, LimiteEstagnacaoSegundos));

    public TimeSpan LimiteIntegracaoStale => TimeSpan.FromSeconds(Math.Max(60, LimiteIntegracaoStaleSegundos));
}

/// <summary>Estado de SAÚDE de um worker (Prompt 31, seções 20, 21) — heartbeat, último sucesso/erro, contadores. Sem payload.</summary>
public sealed record EstadoSaudeWorker(
    string Worker, bool Ativo, DateTimeOffset? IniciadoEmUtc, DateTimeOffset? UltimaBatidaUtc, DateTimeOffset? UltimoSucessoUtc,
    long TotalProcessado, DateTimeOffset? UltimoErroUtc, string? UltimoErroCodigo, long TotalErros, int Reinicios)
{
    /// <summary>Estagnado? Ativo, mas sem batida além do limite (§21).</summary>
    public bool Estagnado(TimeSpan limite, DateTimeOffset agoraUtc)
        => Ativo && UltimaBatidaUtc is { } ultima && (agoraUtc - ultima) > limite;
}

/// <summary>
/// Registro de SAÚDE dos WORKERS (Prompt 31, seções 20, 21) — singleton em memória, thread-safe. Cada worker
/// reporta início/batida/sucesso/erro/encerramento; health checks e métricas leem daqui (sem custo externo).
/// </summary>
public interface IRegistroSaudeWorkers
{
    void RegistrarInicio(string worker, DateTimeOffset agoraUtc);

    /// <summary>Heartbeat: o worker está vivo e no ciclo (§20/§21).</summary>
    void RegistrarBatida(string worker, DateTimeOffset agoraUtc);

    /// <summary>Ciclo bem-sucedido (opcionalmente com quantos itens foram processados).</summary>
    void RegistrarSucesso(string worker, long itensProcessados, DateTimeOffset agoraUtc);

    /// <summary>Falha do ciclo — só o CÓDIGO/tipo do erro (nunca payload/PII — §3).</summary>
    void RegistrarErro(string worker, string codigoErro, DateTimeOffset agoraUtc);

    void RegistrarEncerramento(string worker, DateTimeOffset agoraUtc);

    EstadoSaudeWorker? Obter(string worker);

    IReadOnlyList<EstadoSaudeWorker> Todos();
}

/// <summary>
/// Saúde dos workers PERSISTIDA no catálogo: o host do Worker grava, a cada batida, o snapshot do seu registro em
/// memória; a Web (outro processo) lê para mostrar em Operações se o Worker está vivo. Só nome técnico, contadores,
/// timestamps e códigos de erro (nunca PII/payload).
/// </summary>
public interface IRepositorioSaudeWorkersPersistida
{
    Task GravarAsync(IReadOnlyList<EstadoSaudeWorker> estados, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EstadoSaudeWorker>> ListarAsync(CancellationToken cancellationToken = default);
}

/// <summary>Estado da ÚLTIMA INTEGRAÇÃO externa (Prompt 31, seções 16, 44) — separa conectividade de "última bem-sucedida".</summary>
public sealed record EstadoUltimaIntegracao(
    string Integracao, DateTimeOffset? UltimoSucessoUtc, DateTimeOffset? UltimaFalhaUtc, string? UltimoErroCodigo, long TotalSucesso, long TotalFalha);

/// <summary>
/// Registro da ÚLTIMA INTEGRAÇÃO externa bem-sucedida (Prompt 31, seções 16, 17, 44) — evita health checks que
/// disparam chamadas externas AGRESSIVAS. Os clientes de APIs externas reportam sucesso/falha; a saúde é derivada
/// do FRESCOR do último sucesso. Nunca guarda credencial/token/payload (§3/§43).
/// </summary>
public interface IRegistroUltimaIntegracao
{
    void RegistrarSucesso(string integracao, DateTimeOffset agoraUtc);

    void RegistrarFalha(string integracao, string codigoErro, DateTimeOffset agoraUtc);

    EstadoUltimaIntegracao? Obter(string integracao);

    IReadOnlyList<EstadoUltimaIntegracao> Todos();
}

/// <summary>Severidade de um alerta operacional (Prompt 31, seção 40).</summary>
public enum SeveridadeAlerta
{
    Info = 1,
    Warning = 2,
    Alto = 3,
    Critico = 4,
}

/// <summary>
/// ALERTA operacional (Prompt 31, seções 39, 40) — condição que exige ação. Só metadados técnicos: componente,
/// severidade, código, descrição SANITIZADA, tenant afetado (referência), correlação. NUNCA payload/segredo (§3/§23).
/// </summary>
public sealed record AlertaOperacional(
    string Codigo, SeveridadeAlerta Severidade, string Componente, string Descricao, Guid? TenantAfetadoId = null, string? CorrelationId = null);
