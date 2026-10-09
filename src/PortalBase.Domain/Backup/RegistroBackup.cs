using System.Text.RegularExpressions;
using PortalBase.Domain.Comum;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Backup;

/// <summary>
/// CATÁLOGO DE BACKUPS (Prompt 50, Fase 13) — um registro por artefato de backup, no banco de
/// CATÁLOGO. Guarda somente METADADOS auditáveis (identidade, tipo, tenant, hash de integridade,
/// tamanho, versão, local LÓGICO, retenção, resultado do teste de restauração). NUNCA guarda
/// segredo, senha, token, chave ou connection string: a criptografia é referenciada por
/// <see cref="ChaveRef"/> (nome no cofre), jamais pelo valor da chave (regra 7).
///
/// Regra fundamental do prompt: BACKUP ≠ RESTORE. Um artefato só é candidato a restauração quando
/// <see cref="StatusBackup.Concluido"/> COM <see cref="HashSha256"/>; a confiança plena exige o
/// <see cref="RestoreTestado"/> (teste de restauração real/controlado).
/// </summary>
public sealed partial class RegistroBackup
{
    private const int LimiteAlvo = 128;
    private const int LimiteLocal = 256;
    private const int LimiteVersao = 200;
    private const int LimiteChaveRef = 200;
    private const int LimiteErro = 512;

    private RegistroBackup()
    {
    }

    private RegistroBackup(Guid id, TipoBackup tipo, Guid? tenantId, string alvoLogico, DateTimeOffset agoraUtc)
    {
        Id = id;
        Tipo = tipo;
        TenantId = tenantId;
        AlvoLogico = alvoLogico;
        Status = StatusBackup.Iniciado;
        AlgoritmoHash = "SHA-256";
        IniciadoEmUtc = agoraUtc;
        AtualizadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public Guid Id { get; private set; }

    public TipoBackup Tipo { get; private set; }

    /// <summary>Tenant do backup — OBRIGATÓRIO para <see cref="TipoBackup.TenantCompleto"/>/<see cref="TipoBackup.Storage"/>, e NULO para o catálogo.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>Alvo LÓGICO (nome da base física ou raiz de storage) — nunca credencial nem connection string.</summary>
    public string AlvoLogico { get; private set; } = null!;

    public StatusBackup Status { get; private set; }

    public DateTimeOffset IniciadoEmUtc { get; private set; }

    public DateTimeOffset? ConcluidoEmUtc { get; private set; }

    public DateTimeOffset AtualizadoEmUtc { get; private set; }

    public long TamanhoBytes { get; private set; }

    /// <summary>Digest de integridade (64 hex minúsculos) — a barreira contra corrupção. Nulo até concluir.</summary>
    public string? HashSha256 { get; private set; }

    public string AlgoritmoHash { get; private set; } = "SHA-256";

    /// <summary>Versão de schema/aplicação do artefato (para compatibilidade de restore).</summary>
    public string? Versao { get; private set; }

    /// <summary>Local de armazenamento LÓGICO (rótulo do destino: primário/offsite) — nunca caminho com credencial.</summary>
    public string? LocalArmazenamentoLogico { get; private set; }

    public bool Criptografado { get; private set; }

    /// <summary>Referência (nome no cofre) da chave de cifra — NUNCA o valor da chave (regra 7/11).</summary>
    public string? ChaveRef { get; private set; }

    /// <summary>Artefato gravado em storage imutável/WORM/versionado quando disponível (Fase 10).</summary>
    public bool Imutavel { get; private set; }

    /// <summary>Data-limite de retenção (Fase 9): antes dela, o artefato nunca é descartado.</summary>
    public DateTimeOffset? RetencaoAteUtc { get; private set; }

    /// <summary>Teste de restauração já executado com sucesso (Fase 13/18) — BACKUP ≠ RESTORE.</summary>
    public bool RestoreTestado { get; private set; }

    public DateTimeOffset? RestoreTestadoEmUtc { get; private set; }

    /// <summary>Último erro sanitizado (sem segredos/connection string).</summary>
    public string? UltimoErro { get; private set; }

    /// <summary>Situação da cópia externa (offsite); nulo = cópia externa não configurada no servidor.</summary>
    public SituacaoCopiaExterna? CopiaExterna { get; private set; }

    public DateTimeOffset? CopiaExternaEmUtc { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    /// <summary>Somente um artefato CONCLUÍDO, com hash e não corrompido, é candidato a restauração.</summary>
    public bool CandidatoRestauracao => Status == StatusBackup.Concluido && !string.IsNullOrEmpty(HashSha256);

    /// <summary>
    /// Inicia o registro de um backup. Garante a invariante tenant×tipo: backup de tenant/storage EXIGE
    /// tenant; backup de catálogo NÃO tem tenant. Sem isso, um registro cruzaria a fronteira A↔B (Fase 29).
    /// </summary>
    public static RegistroBackup Iniciar(TipoBackup tipo, Guid? tenantId, string alvoLogico, DateTimeOffset agoraUtc, Guid? id = null)
    {
        var alvo = Exigir.TextoObrigatorioComLimite(alvoLogico, LimiteAlvo, nameof(AlvoLogico));

        if (tipo is not (TipoBackup.CatalogoCompleto or TipoBackup.TenantCompleto or TipoBackup.Storage))
        {
            throw new EntradaInvalidaException($"Tipo de backup desconhecido: '{tipo}'.");
        }

        if (tipo == TipoBackup.CatalogoCompleto && tenantId is not null)
        {
            throw new EntradaInvalidaException("Backup de catálogo não pode ser associado a um tenant.");
        }

        if (tipo is TipoBackup.TenantCompleto or TipoBackup.Storage && (tenantId is null || tenantId.Value == Guid.Empty))
        {
            throw new EntradaInvalidaException("Backup de tenant/storage exige um tenant válido.");
        }

        return new RegistroBackup(id ?? Guid.NewGuid(), tipo, tenantId, alvo, agoraUtc);
    }

    /// <summary>Conclui o backup com o hash de integridade e os metadados. Só a partir de <see cref="StatusBackup.Iniciado"/>.</summary>
    public void Concluir(
        string hashSha256,
        long tamanhoBytes,
        string versao,
        string localArmazenamentoLogico,
        bool criptografado,
        string? chaveRef,
        bool imutavel,
        DateTimeOffset retencaoAteUtc,
        DateTimeOffset agoraUtc)
    {
        if (Status != StatusBackup.Iniciado)
        {
            throw new EstadoInvalidoException($"Só um backup iniciado pode ser concluído (estado atual: '{Status}').");
        }

        if (!HashValido(hashSha256))
        {
            throw new EntradaInvalidaException("Hash de integridade deve ser SHA-256 (64 caracteres hexadecimais).");
        }

        if (tamanhoBytes < 0)
        {
            throw new EntradaInvalidaException("Tamanho do backup não pode ser negativo.");
        }

        if (criptografado)
        {
            if (string.IsNullOrWhiteSpace(chaveRef))
            {
                throw new EntradaInvalidaException("Backup criptografado exige a referência da chave (nome no cofre).");
            }

            ChaveRef = Truncar(chaveRef, LimiteChaveRef); // apenas a REFERÊNCIA (nome no cofre), nunca a chave
        }
        else
        {
            ChaveRef = null;
        }

        HashSha256 = hashSha256.ToLowerInvariant();
        TamanhoBytes = tamanhoBytes;
        Versao = Exigir.TextoObrigatorioComLimite(versao, LimiteVersao, nameof(Versao));
        LocalArmazenamentoLogico = Exigir.TextoObrigatorioComLimite(localArmazenamentoLogico, LimiteLocal, nameof(LocalArmazenamentoLogico));
        Criptografado = criptografado;
        Imutavel = imutavel;
        RetencaoAteUtc = retencaoAteUtc;
        Status = StatusBackup.Concluido;
        ConcluidoEmUtc = agoraUtc;
        UltimoErro = null;
        Tocar(agoraUtc);
    }

    /// <summary>Marca falha na geração/gravação. O artefato não é restaurável.</summary>
    public void Falhar(string erroSanitizado, DateTimeOffset agoraUtc)
    {
        Status = StatusBackup.Falhou;
        UltimoErro = Truncar(Exigir.TextoObrigatorio(erroSanitizado, nameof(UltimoErro)), LimiteErro);
        Tocar(agoraUtc);
    }

    /// <summary>Marca corrupção detectada por verificação de integridade (Fase 26). NUNCA restaurar depois disto.</summary>
    public void MarcarCorrompido(string motivoSanitizado, DateTimeOffset agoraUtc)
    {
        Status = StatusBackup.Corrompido;
        UltimoErro = Truncar(Exigir.TextoObrigatorio(motivoSanitizado, nameof(UltimoErro)), LimiteErro);
        Tocar(agoraUtc);
    }

    /// <summary>
    /// Registra a cópia externa (offsite) do artefato: gravada e conferida pelo hash, ou falha. Só de um backup concluído —
    /// um artefato que não terminou não tem o que copiar.
    /// </summary>
    public void RegistrarCopiaExterna(bool sucesso, DateTimeOffset agoraUtc)
    {
        if (Status != StatusBackup.Concluido)
        {
            throw new EstadoInvalidoException("Só um backup concluído pode ter cópia externa registrada.");
        }

        CopiaExterna = sucesso ? SituacaoCopiaExterna.Concluida : SituacaoCopiaExterna.Falhou;
        CopiaExternaEmUtc = agoraUtc;
        Tocar(agoraUtc);
    }

    /// <summary>Registra o resultado de um TESTE de restauração (Fase 13/18). BACKUP ≠ RESTORE.</summary>
    public void RegistrarTesteRestauracao(bool sucesso, DateTimeOffset agoraUtc)
    {
        if (Status != StatusBackup.Concluido)
        {
            throw new EstadoInvalidoException("Só um backup concluído pode ter teste de restauração registrado.");
        }

        RestoreTestado = sucesso;
        RestoreTestadoEmUtc = agoraUtc;
        Tocar(agoraUtc);
    }

    /// <summary>Marca o artefato como expirado pela política de retenção (Fase 9).</summary>
    public void Expirar(DateTimeOffset agoraUtc)
    {
        Status = StatusBackup.Expirado;
        Tocar(agoraUtc);
    }

    /// <summary>O artefato está dentro da janela de retenção na data informada?</summary>
    public bool DentroDaRetencao(DateTimeOffset agoraUtc)
        => RetencaoAteUtc is null || RetencaoAteUtc.Value > agoraUtc;

    public static bool HashValido(string? hash)
        => !string.IsNullOrWhiteSpace(hash) && RegexHash().IsMatch(hash);

    private static string Truncar(string valor, int limite)
        => valor.Length <= limite ? valor : valor[..limite];

    private void Tocar(DateTimeOffset agoraUtc)
    {
        AtualizadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex RegexHash();
}
