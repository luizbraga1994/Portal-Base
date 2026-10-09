namespace PortalBase.Application.Backup;

/// <summary>
/// Configuração da política de BACKUP/RESTORE/DR (Prompt 50). Tudo é DIRIGIDO POR CONFIGURAÇÃO
/// (Fases 6, 9, 11): nada de períodos arbitrários fixados em código. Não guarda segredo — apenas a
/// REFERÊNCIA da chave de cifra (nome no cofre); o valor é resolvido em runtime (regra 7/11).
/// </summary>
public sealed class OpcoesBackup
{
    public const string Secao = "Backup";

    /// <summary>Retenção (dias) de um backup diário. Padrão conservador; ajuste por política operacional.</summary>
    public int RetencaoDiariaDias { get; set; } = 7;

    /// <summary>Retenção (dias) de um backup semanal.</summary>
    public int RetencaoSemanalDias { get; set; } = 35;

    /// <summary>Retenção (dias) de um backup mensal.</summary>
    public int RetencaoMensalDias { get; set; } = 365;

    /// <summary>
    /// Limite de backups simultâneos (Fase 6): protege CPU/IO/memória/MySQL/storage. Nunca centenas
    /// de backups em paralelo. Mínimo efetivo de 1.
    /// </summary>
    public int ParalelismoMaximo { get; set; } = 2;

    /// <summary>Backups devem ser criptografados em repouso (Fase 11). Padrão seguro: verdadeiro.</summary>
    public bool CriptografarEmRepouso { get; set; } = true;

    /// <summary>Referência (nome no cofre) da chave AES-256 de cifra dos backups — NUNCA o valor (regra 7).</summary>
    public string ChaveCriptografiaRef { get; set; } = string.Empty;

    /// <summary>Rótulo LÓGICO do destino primário de backup (nunca caminho com credencial).</summary>
    public string LocalPrimario { get; set; } = "primario";

    /// <summary>
    /// Obsoleto: o destino externo é <c>ArmazenamentoBackup:DiretorioOffsite</c> (cópia real, conferida pelo hash —
    /// BKP-003). Mantido só para não quebrar configurações antigas; não liga nada.
    /// </summary>
    public string LocalOffsite { get; set; } = string.Empty;

    /// <summary>Bloquear backup enquanto houver migration em andamento (Fase 8). Padrão seguro: verdadeiro.</summary>
    public bool BloquearDuranteMigracao { get; set; } = true;

    /// <summary>
    /// Liga o BACKUP AGENDADO no host do Worker (catálogo + todos os contribuintes provisionados, uma vez por dia).
    /// Padrão: desligado — só ligue com o executor físico configurado (<c>Backup:ExecutorMySql:Habilitado</c>).
    /// </summary>
    public bool AgendamentoHabilitado { get; set; }

    /// <summary>
    /// Horário diário (UTC, <c>HH:mm</c>) do backup agendado. Padrão 03:00 UTC (madrugada no Brasil). Valor
    /// inválido ⇒ o agendamento não roda (fail-closed) e o motivo aparece em Operações.
    /// </summary>
    public string HorarioAgendamentoUtc { get; set; } = "03:00";

    /// <summary>
    /// Tentativas por janela diária quando o backup FALHA (ex.: migração em andamento, executor fora). Entre uma e
    /// outra, <see cref="IntervaloEntreTentativasMinutos"/>. Limites: 1 a 10.
    /// </summary>
    public int TentativasPorJanela { get; set; } = 3;

    /// <summary>Minutos entre tentativas na mesma janela (limites: 1 a 240).</summary>
    public int IntervaloEntreTentativasMinutos { get; set; } = 15;

    /// <summary>
    /// Nome da base do CATÁLOGO para o backup agendado. Vazio ⇒ o da conexão do catálogo configurada no servidor
    /// (nunca vem do cliente).
    /// </summary>
    public string NomeBaseCatalogo { get; set; } = string.Empty;
}
