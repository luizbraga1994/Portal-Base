using System.Globalization;
using PortalBase.Domain.Comum;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Incidentes;

// ============================================================================
// REGISTRO DE INCIDENTE DE SEGURANÇA (Prompt 30, seções 45-47) — infraestrutura TÉCNICA para DETECTAR,
// REGISTRAR, PRESERVAR EVIDÊNCIAS, CLASSIFICAR e ACOMPANHAR incidentes. É governança de PLATAFORMA
// (append-only, como a auditoria): a decisão de haver "risco/dano relevante" ao titular é HUMANA e nunca
// automatizada (§46). NÃO é um workflow jurídico completo nem comunica a ANPD — apenas preserva informação
// suficiente para eventual comunicação/regulação. NUNCA armazena payload/PII bruta: guarda descrições
// sanitizadas + HASH/REFERÊNCIA das evidências (§47).
// ============================================================================

/// <summary>Distingue incidente puramente técnico de incidente COM dados pessoais (Prompt 30, seção 46).</summary>
public enum TipoIncidente
{
    /// <summary>Incidente TÉCNICO (sem dados pessoais identificados).</summary>
    Tecnico = 1,

    /// <summary>Incidente COM DADOS PESSOAIS — sujeito a avaliação de risco/dano ao titular (§46).</summary>
    ComDadosPessoais = 2,
}

public enum SeveridadeIncidente
{
    Informativo = 1,
    Baixo = 2,
    Medio = 3,
    Alto = 4,
    Critico = 5,
}

/// <summary>Ciclo de vida do incidente (Prompt 30, seção 45) — detecção → análise → contenção → encerramento.</summary>
public enum StatusIncidente
{
    Detectado = 1,
    EmAnalise = 2,
    Contido = 3,
    Encerrado = 4,
}

/// <summary>
/// Avaliação HUMANA de risco/dano relevante ao titular (Prompt 30, seção 46) — nunca concluída
/// automaticamente. O default é "não avaliado" (fail-closed: não presume ausência de risco).
/// </summary>
public enum ClassificacaoRiscoTitular
{
    NaoAvaliado = 1,
    SemRiscoRelevante = 2,
    ComRiscoOuDanoRelevante = 3,
}

/// <summary>Tipo de entrada na TIMELINE append-only do incidente (Prompt 30, seções 45, 47).</summary>
public enum TipoEntradaTimeline
{
    Deteccao = 1,
    Analise = 2,
    Contencao = 3,
    Classificacao = 4,
    Evidencia = 5,
    Nota = 6,
    Encerramento = 7,
}

/// <summary>
/// Entrada IMUTÁVEL da timeline de um incidente (Prompt 30, seções 45, 47) — quem/quando/o quê, sanitizado.
/// Nunca é apagada nem alterada: preserva a trilha para forense/accountability.
/// </summary>
public sealed class EventoTimelineIncidente
{
    private EventoTimelineIncidente()
    {
    }

    internal EventoTimelineIncidente(Guid id, Guid incidenteId, int sequencia, TipoEntradaTimeline tipo, string? atorId, string descricao, DateTimeOffset ocorridoEmUtc)
    {
        Id = id;
        IncidenteId = incidenteId;
        Sequencia = sequencia;
        Tipo = tipo;
        AtorId = atorId;
        Descricao = descricao;
        OcorridoEmUtc = ocorridoEmUtc;
    }

    public Guid Id { get; private set; }

    public Guid IncidenteId { get; private set; }

    public int Sequencia { get; private set; }

    public TipoEntradaTimeline Tipo { get; private set; }

    public string? AtorId { get; private set; }

    public string Descricao { get; private set; } = null!;

    public DateTimeOffset OcorridoEmUtc { get; private set; }
}

/// <summary>
/// INCIDENTE DE SEGURANÇA (Prompt 30, seções 45-47) — agregado append-only de governança de plataforma.
/// Registra código, tenant afetado (referência — pode ser nulo em incidente global), tipo, severidade, status,
/// categorias de dados envolvidas, avaliação HUMANA de risco ao titular (§46), evidências por HASH/REFERÊNCIA
/// (§47), responsável, correlação e a TIMELINE imutável. NUNCA guarda payload/PII bruta (§41/§47).
/// </summary>
public sealed class IncidenteSeguranca
{
    private readonly List<EventoTimelineIncidente> _timeline = [];

    private IncidenteSeguranca()
    {
    }

    private IncidenteSeguranca(
        Guid id, string codigo, Guid? tenantAfetadoId, TipoIncidente tipo, SeveridadeIncidente severidade,
        string titulo, string descricao, string? sistemasAfetados, string? categoriasDados, string abertoPorId, string? correlationId, DateTimeOffset agoraUtc)
    {
        Id = id;
        Codigo = codigo;
        TenantAfetadoId = tenantAfetadoId;
        Tipo = tipo;
        Severidade = severidade;
        Titulo = titulo;
        Descricao = descricao;
        SistemasAfetados = sistemasAfetados;
        CategoriasDados = categoriasDados;
        AbertoPorId = abertoPorId;
        CorrelationId = correlationId;
        Status = StatusIncidente.Detectado;
        ClassificacaoRisco = ClassificacaoRiscoTitular.NaoAvaliado;
        DetectadoEmUtc = agoraUtc;
        AtualizadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public Guid Id { get; private set; }

    public string Codigo { get; private set; } = null!;

    /// <summary>Tenant afetado (referência). Nulo em incidente de plataforma/global (§45).</summary>
    public Guid? TenantAfetadoId { get; private set; }

    public TipoIncidente Tipo { get; private set; }

    public SeveridadeIncidente Severidade { get; private set; }

    public StatusIncidente Status { get; private set; }

    public string Titulo { get; private set; } = null!;

    /// <summary>Descrição SANITIZADA (nunca payload/PII bruta — §41/§47).</summary>
    public string Descricao { get; private set; } = null!;

    public string? SistemasAfetados { get; private set; }

    /// <summary>Categorias de dados envolvidas (CSV sanitizado — §4). Ex.: "Cadastrais,Fiscais".</summary>
    public string? CategoriasDados { get; private set; }

    public ClassificacaoRiscoTitular ClassificacaoRisco { get; private set; }

    /// <summary>Justificativa da classificação de risco (decisão humana — §46).</summary>
    public string? JustificativaClassificacao { get; private set; }

    public string? ResponsavelId { get; private set; }

    public string AbertoPorId { get; private set; } = null!;

    public string? CorrelationId { get; private set; }

    /// <summary>Hash das evidências preservadas (§47) — nunca a evidência em si.</summary>
    public string? HashEvidencias { get; private set; }

    /// <summary>Referência às evidências em armazenamento seguro (§47) — nunca inline.</summary>
    public string? ReferenciaEvidencias { get; private set; }

    public string? ResumoEncerramento { get; private set; }

    public DateTimeOffset DetectadoEmUtc { get; private set; }

    public DateTimeOffset AtualizadoEmUtc { get; private set; }

    public DateTimeOffset? EncerradoEmUtc { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public IReadOnlyList<EventoTimelineIncidente> Timeline => _timeline;

    public bool EstaEncerrado => Status == StatusIncidente.Encerrado;

    public bool EnvolveDadosPessoais => Tipo == TipoIncidente.ComDadosPessoais;

    /// <summary>REGISTRA um incidente recém-DETECTADO (§45). Estado inicial <see cref="StatusIncidente.Detectado"/>.</summary>
    public static IncidenteSeguranca Registrar(
        string? codigo, Guid? tenantAfetadoId, TipoIncidente tipo, SeveridadeIncidente severidade, string titulo, string descricao,
        string? sistemasAfetados, string? categoriasDados, string abertoPorId, string? correlationId, DateTimeOffset agoraUtc, Guid? id = null)
    {
        var codigoFinal = string.IsNullOrWhiteSpace(codigo)
            ? "INC-" + Guid.NewGuid().ToString("N")[..10].ToUpper(CultureInfo.InvariantCulture)
            : Exigir.TextoObrigatorioComLimite(codigo, 32, nameof(Codigo));

        var incidente = new IncidenteSeguranca(
            id ?? Guid.NewGuid(), codigoFinal, tenantAfetadoId, tipo, severidade,
            Exigir.TextoObrigatorioComLimite(titulo, 200, nameof(Titulo)),
            Sanitizar(descricao, 2000, nameof(Descricao), obrigatorio: true)!,
            sistemasAfetados is null ? null : Exigir.TamanhoMaximo(sistemasAfetados, 500, nameof(SistemasAfetados)),
            categoriasDados is null ? null : Exigir.TamanhoMaximo(categoriasDados, 300, nameof(CategoriasDados)),
            Exigir.TextoObrigatorioComLimite(abertoPorId, 64, nameof(AbertoPorId)),
            correlationId is null ? null : Exigir.TamanhoMaximo(correlationId, 64, nameof(CorrelationId)),
            agoraUtc);

        incidente.Anexar(TipoEntradaTimeline.Deteccao, abertoPorId, "Incidente detectado e registrado.", agoraUtc);
        return incidente;
    }

    /// <summary>Inicia a ANÁLISE (Detectado → EmAnalise). Append-only (§45).</summary>
    public void IniciarAnalise(string atorId, string? nota, DateTimeOffset agoraUtc)
    {
        ExigirNaoEncerrado();
        Status = StatusIncidente.EmAnalise;
        Anexar(TipoEntradaTimeline.Analise, atorId, Sanitizar(nota, 1000, nameof(nota), obrigatorio: false) ?? "Análise iniciada.", agoraUtc);
        Tocar(agoraUtc);
    }

    /// <summary>Registra uma medida de CONTENÇÃO (→ Contido). Append-only (§45).</summary>
    public void RegistrarContencao(string atorId, string medida, DateTimeOffset agoraUtc)
    {
        ExigirNaoEncerrado();
        Status = StatusIncidente.Contido;
        Anexar(TipoEntradaTimeline.Contencao, atorId, Sanitizar(medida, 1000, nameof(medida), obrigatorio: true)!, agoraUtc);
        Tocar(agoraUtc);
    }

    /// <summary>
    /// CLASSIFICA o risco/dano ao titular (Prompt 30, seção 46) — DECISÃO HUMANA registrada com justificativa,
    /// responsável e timestamp. Se marcar dados pessoais, o incidente passa a <see cref="TipoIncidente.ComDadosPessoais"/>.
    /// A conclusão jurídica NÃO é automatizada: o chamador (humano autorizado) fornece a classificação.
    /// </summary>
    public void ClassificarRiscoTitular(string atorId, bool envolveDadosPessoais, ClassificacaoRiscoTitular classificacao, string justificativa, string responsavelId, DateTimeOffset agoraUtc)
    {
        ExigirNaoEncerrado();
        if (classificacao == ClassificacaoRiscoTitular.NaoAvaliado)
        {
            throw new EntradaInvalidaException("A classificação de risco exige uma decisão explícita (não 'NaoAvaliado').");
        }

        Tipo = envolveDadosPessoais ? TipoIncidente.ComDadosPessoais : Tipo;
        ClassificacaoRisco = classificacao;
        JustificativaClassificacao = Sanitizar(justificativa, 2000, nameof(justificativa), obrigatorio: true);
        ResponsavelId = Exigir.TextoObrigatorioComLimite(responsavelId, 64, nameof(ResponsavelId));
        Anexar(TipoEntradaTimeline.Classificacao, atorId, string.Create(CultureInfo.InvariantCulture, $"Classificação: {classificacao} (dados pessoais: {envolveDadosPessoais})."), agoraUtc);
        Tocar(agoraUtc);
    }

    /// <summary>Associa EVIDÊNCIAS por HASH + REFERÊNCIA (Prompt 30, seção 47) — nunca a evidência bruta.</summary>
    public void AssociarEvidencias(string atorId, string hash, string referencia, DateTimeOffset agoraUtc)
    {
        ExigirNaoEncerrado();
        HashEvidencias = Exigir.TextoObrigatorioComLimite(hash, 64, nameof(HashEvidencias));
        ReferenciaEvidencias = Exigir.TextoObrigatorioComLimite(referencia, 256, nameof(ReferenciaEvidencias));
        Anexar(TipoEntradaTimeline.Evidencia, atorId, "Evidências preservadas (hash + referência).", agoraUtc);
        Tocar(agoraUtc);
    }

    /// <summary>Adiciona uma NOTA à timeline (append-only).</summary>
    public void AdicionarNota(string atorId, string nota, DateTimeOffset agoraUtc)
    {
        ExigirNaoEncerrado();
        Anexar(TipoEntradaTimeline.Nota, atorId, Sanitizar(nota, 1000, nameof(nota), obrigatorio: true)!, agoraUtc);
        Tocar(agoraUtc);
    }

    /// <summary>ENCERRA o incidente (→ Encerrado). Terminal; a timeline permanece imutável (§45/§47).</summary>
    public void Encerrar(string atorId, string resumo, DateTimeOffset agoraUtc)
    {
        ExigirNaoEncerrado();
        if (Status == StatusIncidente.Detectado)
        {
            throw new EstadoInvalidoException("Encerre apenas após análise/contenção (não direto da detecção).");
        }

        ResumoEncerramento = Sanitizar(resumo, 2000, nameof(resumo), obrigatorio: true);
        Status = StatusIncidente.Encerrado;
        EncerradoEmUtc = agoraUtc;
        Anexar(TipoEntradaTimeline.Encerramento, atorId, ResumoEncerramento!, agoraUtc);
        Tocar(agoraUtc);
    }

    private void Anexar(TipoEntradaTimeline tipo, string? atorId, string descricao, DateTimeOffset agoraUtc)
        => _timeline.Add(new EventoTimelineIncidente(
            Guid.NewGuid(), Id, _timeline.Count + 1, tipo,
            atorId is null ? null : Exigir.TamanhoMaximo(atorId, 64, nameof(atorId)),
            descricao, agoraUtc));

    private void ExigirNaoEncerrado()
    {
        if (EstaEncerrado)
        {
            throw new EstadoInvalidoException("Incidente encerrado é imutável (append-only).");
        }
    }

    private static string? Sanitizar(string? valor, int max, string campo, bool obrigatorio)
    {
        var limpo = (valor ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(limpo))
        {
            return obrigatorio ? throw new EntradaInvalidaException($"{campo} é obrigatório.") : null;
        }

        return limpo.Length > max ? limpo[..max] : limpo;
    }

    private void Tocar(DateTimeOffset agoraUtc)
    {
        AtualizadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }
}
