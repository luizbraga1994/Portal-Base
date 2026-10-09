using PortalBase.Domain.Comum;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Arquivos;

/// <summary>Estado do arquivo de importação (máquina de estados server-side — Prompt 08, seção 15).</summary>
public enum EstadoArquivo
{
    Recebido = 1,
    Rejeitado = 2,
    Armazenado = 3,
    Processando = 4,
    Processado = 5,
    Falhado = 6,
    Expirado = 7,
}

/// <summary>Resultado do antivírus registrado no metadado.</summary>
public enum ResultadoScan
{
    NaoEscaneado = 0,
    Limpo = 1,
    Infectado = 2,
    Indisponivel = 3,
    Erro = 4,
}

/// <summary>
/// Metadados do arquivo de importação, no banco do PRÓPRIO contribuinte (Prompt 08, seções 18,
/// 19). Guarda apenas metadados — NUNCA o conteúdo (o binário vai para storage privado
/// tenant-scoped). Agregado encapsulado. O <see cref="Sha256"/> é único no banco do tenant
/// (idempotência tenant-scoped — seção 17). Nomes já chegam sanitizados/seguros (seção 6).
/// </summary>
public sealed class ArquivoImportacao
{
    private const int LimiteNome = 260;
    private const int LimiteTexto = 128;

    private ArquivoImportacao()
    {
    }

    private ArquivoImportacao(
        Guid id,
        string nomeOriginalSanitizado,
        string nomeSeguro,
        string extensao,
        string contentTypeDetectado,
        long tamanhoBytes,
        string sha256,
        Guid usuarioId,
        string correlationId,
        int categoriaRetencao,
        DateTimeOffset agoraUtc,
        DateTimeOffset expiraEmUtc)
    {
        Id = id;
        NomeOriginalSanitizado = nomeOriginalSanitizado;
        NomeSeguro = nomeSeguro;
        Extensao = extensao;
        ContentTypeDetectado = contentTypeDetectado;
        TamanhoBytes = tamanhoBytes;
        Sha256 = sha256;
        UsuarioId = usuarioId;
        CorrelationId = correlationId;
        CategoriaRetencao = categoriaRetencao;
        EnviadoEmUtc = agoraUtc;
        AtualizadoEmUtc = agoraUtc;
        ExpiraEmUtc = expiraEmUtc;
        Estado = EstadoArquivo.Recebido;
        ResultadoScan = ResultadoScan.NaoEscaneado;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public Guid Id { get; private set; }

    /// <summary>Nome original SANITIZADO (pode conter PII — nunca ir para log indiscriminado; seção 29).</summary>
    public string NomeOriginalSanitizado { get; private set; } = null!;

    /// <summary>Nome físico GERADO pelo servidor (nunca o original; seção 6).</summary>
    public string NomeSeguro { get; private set; } = null!;

    public string Extensao { get; private set; } = null!;

    public string ContentTypeDetectado { get; private set; } = null!;

    public long TamanhoBytes { get; private set; }

    public string Sha256 { get; private set; } = null!;

    public EstadoArquivo Estado { get; private set; }

    public ResultadoScan ResultadoScan { get; private set; }

    public DateTimeOffset? EscaneadoEmUtc { get; private set; }

    /// <summary>Referência OPACA no storage (server-side) — nunca caminho físico exposto (seção 26).</summary>
    public string? ReferenciaArmazenamento { get; private set; }

    public Guid UsuarioId { get; private set; }

    public string CorrelationId { get; private set; } = null!;

    public int CategoriaRetencao { get; private set; }

    public DateTimeOffset ExpiraEmUtc { get; private set; }

    public string? MotivoRejeicao { get; private set; }

    public DateTimeOffset EnviadoEmUtc { get; private set; }

    public DateTimeOffset AtualizadoEmUtc { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public static ArquivoImportacao Registrar(
        string nomeOriginalSanitizado,
        string nomeSeguro,
        string extensao,
        string contentTypeDetectado,
        long tamanhoBytes,
        string sha256,
        Guid usuarioId,
        string correlationId,
        int categoriaRetencao,
        DateTimeOffset agoraUtc,
        DateTimeOffset expiraEmUtc,
        Guid? id = null)
    {
        if (tamanhoBytes <= 0)
        {
            throw new EntradaInvalidaException("Arquivo vazio.");
        }

        if (usuarioId == Guid.Empty)
        {
            throw new EntradaInvalidaException("Usuário é obrigatório.");
        }

        return new ArquivoImportacao(
            id ?? Guid.NewGuid(),
            Exigir.TextoObrigatorioComLimite(nomeOriginalSanitizado, LimiteNome, nameof(NomeOriginalSanitizado)),
            Exigir.TextoObrigatorioComLimite(nomeSeguro, LimiteTexto, nameof(NomeSeguro)),
            Exigir.TextoObrigatorioComLimite(extensao, 16, nameof(Extensao)),
            Exigir.TextoObrigatorioComLimite(contentTypeDetectado, LimiteTexto, nameof(ContentTypeDetectado)),
            tamanhoBytes,
            Exigir.TextoObrigatorioComLimite(sha256, 64, nameof(Sha256)),
            usuarioId,
            Exigir.TextoObrigatorioComLimite(correlationId, 64, nameof(CorrelationId)),
            categoriaRetencao,
            agoraUtc,
            expiraEmUtc);
    }

    /// <summary>Registra o resultado do antivírus (transição a partir de Recebido).</summary>
    public void RegistrarScan(ResultadoScan resultado, DateTimeOffset agoraUtc)
    {
        ResultadoScan = resultado;
        EscaneadoEmUtc = agoraUtc;
        Tocar(agoraUtc);
    }

    /// <summary>Armazenamento concluído e arquivo limpo → Armazenado (só a partir de Recebido).</summary>
    public void MarcarArmazenado(string referenciaArmazenamento, DateTimeOffset agoraUtc)
    {
        if (Estado != EstadoArquivo.Recebido)
        {
            throw new EstadoInvalidoException("Só um arquivo recebido pode ser armazenado.");
        }

        ReferenciaArmazenamento = Exigir.TextoObrigatorioComLimite(referenciaArmazenamento, LimiteTexto, nameof(ReferenciaArmazenamento));
        Estado = EstadoArquivo.Armazenado;
        Tocar(agoraUtc);
    }

    /// <summary>Rejeita o arquivo (validação/scan) com motivo sanitizado. Nunca poderá ser processado.</summary>
    public void Rejeitar(string motivoSanitizado, DateTimeOffset agoraUtc)
    {
        ArgumentNullException.ThrowIfNull(motivoSanitizado);
        Estado = EstadoArquivo.Rejeitado;
        MotivoRejeicao = motivoSanitizado.Length > 256 ? motivoSanitizado[..256] : motivoSanitizado;
        Tocar(agoraUtc);
    }

    public bool PodeProcessar => Estado == EstadoArquivo.Armazenado && ResultadoScan == ResultadoScan.Limpo;

    /// <summary>
    /// O resultado do antivírus permite processar sob a política do ambiente? Limpo sempre; sem varredura
    /// (<see cref="ResultadoScan.Indisponivel"/>/<see cref="ResultadoScan.NaoEscaneado"/>) só quando o antivírus NÃO é
    /// obrigatório (decisão de risco formal do ambiente); Infectado ou Erro de varredura NUNCA.
    /// </summary>
    public bool ScanPermiteProcessar(bool antivirusObrigatorio) => ResultadoScan switch
    {
        ResultadoScan.Limpo => true,
        ResultadoScan.Indisponivel or ResultadoScan.NaoEscaneado => !antivirusObrigatorio,
        _ => false,
    };

    /// <summary>
    /// Reivindica o arquivo para PROCESSAMENTO (→ Processando). Aceito a partir de Armazenado, Falhado ou Processado
    /// (reprocessar; o processamento do seu domínio deve ser idempotente). Um arquivo já em
    /// Processando só é retomado quando <paramref name="processandoDesde"/> indica processamento abandonado (o chamador
    /// decide o prazo). Rejeitado/Expirado/Recebido, ou scan não permitido, nunca são processados.
    /// </summary>
    public void IniciarProcessamento(bool antivirusObrigatorio, DateTimeOffset agoraUtc, DateTimeOffset? processandoAbandonadoAntesDe = null)
    {
        var retomavel = Estado == EstadoArquivo.Processando
            && processandoAbandonadoAntesDe is DateTimeOffset limite && AtualizadoEmUtc < limite;
        if (Estado is not (EstadoArquivo.Armazenado or EstadoArquivo.Falhado or EstadoArquivo.Processado) && !retomavel)
        {
            throw new EstadoInvalidoException(Estado == EstadoArquivo.Processando
                ? "O arquivo já está em processamento."
                : "Este arquivo não pode ser processado no estado atual.");
        }

        if (string.IsNullOrWhiteSpace(ReferenciaArmazenamento))
        {
            throw new EstadoInvalidoException("O arquivo não está armazenado.");
        }

        if (!ScanPermiteProcessar(antivirusObrigatorio))
        {
            throw new EstadoInvalidoException("O resultado da verificação de segurança não permite processar o arquivo.");
        }

        Estado = EstadoArquivo.Processando;
        MotivoRejeicao = null;
        Tocar(agoraUtc);
    }

    /// <summary>Processamento concluído (importação Validada ou Parcial) → Processado.</summary>
    public void ConcluirProcessamento(DateTimeOffset agoraUtc)
    {
        ExigirProcessando();
        Estado = EstadoArquivo.Processado;
        MotivoRejeicao = null;
        Tocar(agoraUtc);
    }

    /// <summary>Processamento falhou → Falhado, com motivo GENÉRICO sanitizado (pode ser processado de novo).</summary>
    public void FalharProcessamento(string motivoSanitizado, DateTimeOffset agoraUtc)
    {
        ArgumentNullException.ThrowIfNull(motivoSanitizado);
        ExigirProcessando();
        Estado = EstadoArquivo.Falhado;
        MotivoRejeicao = motivoSanitizado.Length > 256 ? motivoSanitizado[..256] : motivoSanitizado;
        Tocar(agoraUtc);
    }

    private void ExigirProcessando()
    {
        if (Estado != EstadoArquivo.Processando)
        {
            throw new EstadoInvalidoException("O arquivo não está em processamento.");
        }
    }

    private void Tocar(DateTimeOffset agoraUtc)
    {
        AtualizadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }
}
