namespace PortalBase.Application.Producao;

/// <summary>Estado de um portão (gate) de prontidão para produção (Prompt 33, seções 4/48).</summary>
public enum EstadoPortao
{
    /// <summary>Requisito satisfeito com evidência.</summary>
    Ready = 1,

    /// <summary>Satisfeito com condição/ressalva operacional (não bloqueia, mas exige atenção).</summary>
    ReadyWithCondition = 2,

    /// <summary>NÃO satisfeito — requisito falho.</summary>
    NotReady = 3,

    /// <summary>Não homologável neste ambiente por falta de dependência externa (não é PASS).</summary>
    Blocked = 4,

    /// <summary>Não aplicável ao alvo avaliado.</summary>
    NaoAplicavel = 5,
}

/// <summary>Decisão objetiva de prontidão para produção (Prompt 33, seções 5/74).</summary>
public enum DecisaoProntidao
{
    Ready = 1,
    ReadyWithConditions = 2,
    NotReady = 3,
}

/// <summary>
/// Um PORTÃO objetivo de prontidão (Prompt 33). <see cref="Bloqueador"/> marca um requisito cuja
/// falha (§75) torna a produção NOT READY (ex.: cross-tenant, segredo exposto, backup quebrado,
/// integridade desconhecida, rollback impossível, duplicidade). <see cref="Evidencia"/> é a
/// prova (nome de teste, execução, documento) — sem ela um portão nunca deveria ser <see cref="EstadoPortao.Ready"/>.
/// </summary>
public sealed record PortaoProntidao(string Id, string Area, EstadoPortao Estado, string Evidencia, bool Bloqueador);

/// <summary>
/// Avalia os portões de prontidão em uma DECISÃO objetiva (Prompt 33, seções 5/74/75). Regras
/// (nunca "READY só porque o build passou" — §1):
/// <list type="bullet">
///   <item><b>NOT_READY</b> se qualquer portão está <see cref="EstadoPortao.NotReady"/>, ou se um portão
///         BLOQUEADOR não está satisfeito (Ready/N/A);</item>
///   <item><b>READY_WITH_CONDITIONS</b> se não há falha bloqueante, mas há portões
///         <see cref="EstadoPortao.Blocked"/> ou <see cref="EstadoPortao.ReadyWithCondition"/>;</item>
///   <item><b>READY</b> apenas se TODOS os portões são <see cref="EstadoPortao.Ready"/> ou
///         <see cref="EstadoPortao.NaoAplicavel"/>.</item>
/// </list>
/// Lista vazia ⇒ NOT_READY (nada avaliado não é "pronto").
/// </summary>
public static class AvaliadorProntidaoProducao
{
    public static DecisaoProntidao Avaliar(IReadOnlyList<PortaoProntidao> portoes)
    {
        ArgumentNullException.ThrowIfNull(portoes);

        if (portoes.Count == 0)
        {
            return DecisaoProntidao.NotReady;
        }

        var algumNotReady = false;
        var bloqueadorNaoSatisfeito = false;
        var comCondicaoOuBloqueado = false;

        foreach (var p in portoes)
        {
            var satisfeito = p.Estado is EstadoPortao.Ready or EstadoPortao.NaoAplicavel;

            if (p.Estado == EstadoPortao.NotReady)
            {
                algumNotReady = true;
            }

            if (p.Bloqueador && !satisfeito)
            {
                bloqueadorNaoSatisfeito = true;
            }

            if (p.Estado is EstadoPortao.Blocked or EstadoPortao.ReadyWithCondition)
            {
                comCondicaoOuBloqueado = true;
            }
        }

        if (algumNotReady || bloqueadorNaoSatisfeito)
        {
            return DecisaoProntidao.NotReady;
        }

        return comCondicaoOuBloqueado ? DecisaoProntidao.ReadyWithConditions : DecisaoProntidao.Ready;
    }
}

/// <summary>
/// APROVAÇÃO DE PRONTIDÃO PARA PRODUÇÃO (Prompt 33, seção 76) — o registro técnico (não jurídico)
/// da decisão. A <see cref="Decisao"/> é SEMPRE derivada dos portões por
/// <see cref="AvaliadorProntidaoProducao"/>; não é possível "declarar READY" sem que os portões o
/// sustentem. Guarda versão/commit/ambiente/data/executor/portões/riscos residuais para rastreio.
/// </summary>
public sealed class AprovacaoProntidaoProducao
{
    private AprovacaoProntidaoProducao(
        string versao, string commit, AmbienteImplantacao ambiente, DateTimeOffset dataUtc, string executor,
        IReadOnlyList<PortaoProntidao> portoes, IReadOnlyList<string> riscosResiduais, DecisaoProntidao decisao)
    {
        Versao = versao;
        Commit = commit;
        Ambiente = ambiente;
        DataUtc = dataUtc;
        Executor = executor;
        Portoes = portoes;
        RiscosResiduais = riscosResiduais;
        Decisao = decisao;
    }

    public string Versao { get; }

    public string Commit { get; }

    public AmbienteImplantacao Ambiente { get; }

    public DateTimeOffset DataUtc { get; }

    public string Executor { get; }

    public IReadOnlyList<PortaoProntidao> Portoes { get; }

    public IReadOnlyList<string> RiscosResiduais { get; }

    /// <summary>Decisão derivada dos portões — nunca definida manualmente.</summary>
    public DecisaoProntidao Decisao { get; }

    /// <summary>Permite promover a produção (com ou sem condições) — falso se NOT_READY (§75).</summary>
    public bool PermiteProducao => Decisao != DecisaoProntidao.NotReady;

    /// <summary>Prontidão IRRESTRITA — todos os portões Ready/N/A, sem condições pendentes.</summary>
    public bool PermiteProducaoIrrestrita => Decisao == DecisaoProntidao.Ready;

    public static AprovacaoProntidaoProducao Criar(
        string versao, string commit, AmbienteImplantacao ambiente, DateTimeOffset dataUtc, string executor,
        IReadOnlyList<PortaoProntidao> portoes, IReadOnlyList<string>? riscosResiduais = null)
    {
        if (string.IsNullOrWhiteSpace(versao))
        {
            throw new ArgumentException("Versão obrigatória.", nameof(versao));
        }

        if (string.IsNullOrWhiteSpace(commit))
        {
            throw new ArgumentException("Commit obrigatório.", nameof(commit));
        }

        if (string.IsNullOrWhiteSpace(executor))
        {
            throw new ArgumentException("Executor obrigatório.", nameof(executor));
        }

        ArgumentNullException.ThrowIfNull(portoes);

        var decisao = AvaliadorProntidaoProducao.Avaliar(portoes);
        return new AprovacaoProntidaoProducao(
            versao.Trim(), commit.Trim(), ambiente, dataUtc, executor.Trim(),
            portoes, riscosResiduais ?? [], decisao);
    }
}
