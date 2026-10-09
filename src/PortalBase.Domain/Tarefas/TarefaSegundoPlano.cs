using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Tarefas;

/// <summary>Trabalho pesado que roda FORA do circuito da tela (ARQ-006, auditoria de 08/10/2026).</summary>
public enum TipoTarefaSegundoPlano
{
    /// <summary>
    /// Tarefa de EXEMPLO do template (ver <c>ManipuladorTarefaExemplo</c>). Substitua pelos tipos do seu domínio — novos
    /// valores SEMPRE ao final (o valor é gravado no catálogo).
    /// </summary>
    Exemplo = 1,
}

/// <summary>Situação de uma tarefa em segundo plano.</summary>
public enum EstadoTarefaSegundoPlano
{
    Pendente = 1,
    EmExecucao = 2,
    Concluida = 3,
    Falhou = 4,
}

/// <summary>
/// TAREFA EM SEGUNDO PLANO, durável, no catálogo (ARQ-006): o processamento pesado rodava DENTRO do
/// circuito do Blazor — fechar a aba, perder a conexão ou reciclar o pool do IIS interrompia a operação no meio. Agora a
/// tela só ENFILEIRA; um executor no servidor reivindica a tarefa por um prazo (<see cref="LeaseAteUtc"/>, renovado
/// enquanto roda) e grava o resultado. Se o processo morrer, o prazo vence e a tarefa é retomada — até
/// <see cref="MaximoTentativas"/> vezes. Guarda só identificadores e parâmetros técnicos (nunca segredo, conteúdo do
/// arquivo ou dado pessoal); o contribuinte e o solicitante vêm do servidor no momento do pedido (regras 4 e 5).
/// </summary>
public sealed class TarefaSegundoPlano
{
    /// <summary>Execuções (inclusive as interrompidas) antes de desistir.</summary>
    public const int MaximoTentativas = 3;

    public const int TamanhoMaximoParametros = 2000;

    public const int TamanhoMaximoResultado = 4000;

    public const int TamanhoMaximoMotivo = 500;

    public const int TamanhoMaximoChave = 200;

    private TarefaSegundoPlano()
    {
    }

    public Guid Id { get; private set; }

    public TipoTarefaSegundoPlano Tipo { get; private set; }

    /// <summary>Contribuinte (resolvido no servidor no pedido; a execução revalida o acesso do solicitante).</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Quem pediu: a execução roda com a identidade e as permissões DELE (revalidadas na hora).</summary>
    public Guid SolicitanteId { get; private set; }

    /// <summary>Parâmetros técnicos em JSON (ex.: identificador do arquivo). Nunca segredo nem conteúdo.</summary>
    public string Parametros { get; private set; } = null!;

    /// <summary>Identifica o "mesmo pedido": um segundo pedido igual, com o primeiro ainda ativo, reaproveita o primeiro.</summary>
    public string ChaveIdempotencia { get; private set; } = null!;

    public EstadoTarefaSegundoPlano Estado { get; private set; }

    public int Tentativas { get; private set; }

    public DateTimeOffset CriadaEmUtc { get; private set; }

    public DateTimeOffset? IniciadaEmUtc { get; private set; }

    public DateTimeOffset? ConcluidaEmUtc { get; private set; }

    /// <summary>Até quando a execução atual "segura" a tarefa; vencido, outra execução pode retomá-la.</summary>
    public DateTimeOffset? LeaseAteUtc { get; private set; }

    /// <summary>Máquina/processo que executa (diagnóstico).</summary>
    public string? Executor { get; private set; }

    /// <summary>Resultado técnico em JSON (contagens e desfecho — o que a tela mostra).</summary>
    public string? Resultado { get; private set; }

    /// <summary>Motivo sanitizado da falha (sem detalhe interno).</summary>
    public string? Motivo { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public bool Ativa => Estado is EstadoTarefaSegundoPlano.Pendente or EstadoTarefaSegundoPlano.EmExecucao;

    public static TarefaSegundoPlano Criar(
        TipoTarefaSegundoPlano tipo, Guid tenantId, Guid solicitanteId, string parametros, string chaveIdempotencia, DateTimeOffset agoraUtc)
    {
        if (!Enum.IsDefined(tipo))
        {
            throw new EntradaInvalidaException("Tipo de tarefa inválido.");
        }

        if (tenantId == Guid.Empty || solicitanteId == Guid.Empty)
        {
            throw new EntradaInvalidaException("Contribuinte e solicitante são obrigatórios.");
        }

        if (string.IsNullOrWhiteSpace(parametros) || parametros.Length > TamanhoMaximoParametros)
        {
            throw new EntradaInvalidaException("Parâmetros da tarefa inválidos.");
        }

        if (string.IsNullOrWhiteSpace(chaveIdempotencia) || chaveIdempotencia.Length > TamanhoMaximoChave)
        {
            throw new EntradaInvalidaException("Chave da tarefa inválida.");
        }

        return new TarefaSegundoPlano
        {
            Id = Guid.NewGuid(),
            Tipo = tipo,
            TenantId = tenantId,
            SolicitanteId = solicitanteId,
            Parametros = parametros,
            ChaveIdempotencia = chaveIdempotencia,
            Estado = EstadoTarefaSegundoPlano.Pendente,
            CriadaEmUtc = agoraUtc,
            ConcurrencyStamp = Guid.NewGuid(),
        };
    }

    /// <summary>Pode ser reivindicada agora: pendente, ou "em execução" com o prazo vencido (o processo morreu).</summary>
    public bool PodeSerReivindicada(DateTimeOffset agoraUtc)
        => Estado == EstadoTarefaSegundoPlano.Pendente
           || (Estado == EstadoTarefaSegundoPlano.EmExecucao && LeaseAteUtc is { } lease && lease <= agoraUtc);

    /// <summary>
    /// Reivindica para executar. Retorna <c>false</c> (e marca Falhou) se já foram <see cref="MaximoTentativas"/>
    /// execuções — uma tarefa que derruba o processo não fica em ciclo para sempre.
    /// </summary>
    public bool Reivindicar(DateTimeOffset agoraUtc, TimeSpan prazo, string executor)
    {
        if (!PodeSerReivindicada(agoraUtc))
        {
            throw new EstadoInvalidoException("A tarefa não pode ser reivindicada agora.");
        }

        if (Tentativas >= MaximoTentativas)
        {
            Falhar("A tarefa foi interrompida repetidas vezes (reinício do servidor) e não foi concluída. Peça de novo.", agoraUtc);
            return false;
        }

        Tentativas++;
        Estado = EstadoTarefaSegundoPlano.EmExecucao;
        IniciadaEmUtc ??= agoraUtc;
        LeaseAteUtc = agoraUtc.Add(prazo);
        Executor = Limitar(executor, 100);
        ConcurrencyStamp = Guid.NewGuid();
        return true;
    }

    /// <summary>Estende o prazo enquanto a execução continua viva.</summary>
    public void Renovar(DateTimeOffset agoraUtc, TimeSpan prazo)
    {
        if (Estado != EstadoTarefaSegundoPlano.EmExecucao)
        {
            throw new EstadoInvalidoException("Só uma tarefa em execução tem o prazo renovado.");
        }

        LeaseAteUtc = agoraUtc.Add(prazo);
        ConcurrencyStamp = Guid.NewGuid();
    }

    public void Concluir(string? resultado, DateTimeOffset agoraUtc)
    {
        if (Estado != EstadoTarefaSegundoPlano.EmExecucao)
        {
            throw new EstadoInvalidoException("Só uma tarefa em execução é concluída.");
        }

        Estado = EstadoTarefaSegundoPlano.Concluida;
        Resultado = Limitar(resultado, TamanhoMaximoResultado);
        ConcluidaEmUtc = agoraUtc;
        LeaseAteUtc = null;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public void Falhar(string motivo, DateTimeOffset agoraUtc, string? resultado = null)
    {
        if (!Ativa)
        {
            throw new EstadoInvalidoException("A tarefa já terminou.");
        }

        Estado = EstadoTarefaSegundoPlano.Falhou;
        Motivo = Limitar(string.IsNullOrWhiteSpace(motivo) ? "Falha ao executar a tarefa." : motivo, TamanhoMaximoMotivo);
        Resultado = Limitar(resultado, TamanhoMaximoResultado);
        ConcluidaEmUtc = agoraUtc;
        LeaseAteUtc = null;
        ConcurrencyStamp = Guid.NewGuid();
    }

    private static string? Limitar(string? texto, int maximo)
        => texto is null ? null : texto.Length <= maximo ? texto : texto[..maximo];
}
