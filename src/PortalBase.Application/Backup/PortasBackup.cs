using PortalBase.Domain.Backup;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Backup;

/// <summary>
/// Alvo de backup de um tenant (Prompt 50, Fase 5): identidade + base física LÓGICA + estado do ciclo
/// de vida. Nunca carrega credencial/connection string (a conexão é resolvida server-side pelo cofre).
/// </summary>
public sealed record AlvoBackupTenant(Guid TenantId, string NomeBaseFisica, EstadoProvisionamento Estado)
{
    /// <summary>Só tenants provisionados (Pronto/Ativo) têm base física válida ⇒ são backup-ready (Fase 7).</summary>
    public bool BackupReady => Estado is EstadoProvisionamento.Pronto or EstadoProvisionamento.Ativo;
}

/// <summary>
/// Lista/obtém os tenants candidatos a backup (Fase 5/7). NUNCA inclui tenant em provisionamento
/// (base parcial) — um backup de base incompleta não é válido.
/// </summary>
public interface IProvedorAlvosBackup
{
    /// <summary>Tenants BACKUP-READY (provisionados). Ignora Pendente/Provisionando/Falhado.</summary>
    Task<IReadOnlyList<AlvoBackupTenant>> ListarBackupReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>Obtém o alvo de um tenant (para validar metadata na restauração — Fase 15/29). Nulo se inexistente.</summary>
    Task<AlvoBackupTenant?> ObterAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Nome LÓGICO da base do CATÁLOGO (o da conexão configurada no servidor); nulo se não houver conexão.</summary>
    string? NomeBaseCatalogo { get; }
}

/// <summary>Migration em andamento? (Fase 8) — bloqueia backup para não produzir artefato inconsistente.</summary>
public interface IDetectorMigracaoEmAndamento
{
    Task<bool> MigracaoEmAndamentoAsync(Guid? tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// EXECUTOR físico do backup/restore de banco (mysqldump / mysql). A implementação real depende de
/// MySQL + ferramentas no host: quando indisponível, <see cref="Disponivel"/> é falso e a operação
/// física fica BLOCKED (nunca simulada como sucesso). O restore SEMPRE aponta para uma base TEMPORÁRIA
/// (Fase 14/15) — nunca sobrescreve produção.
/// </summary>
public interface IExecutorBackupBanco
{
    /// <summary>Ferramenta + banco disponíveis? Falso ⇒ backup/restore físico BLOCKED neste ambiente.</summary>
    bool Disponivel { get; }

    /// <summary>Gera o dump lógico da base informada para o stream de destino (consistência transacional — Fase 3).</summary>
    Task GerarDumpAsync(string nomeBaseLogica, Stream destino, CancellationToken cancellationToken = default);

    /// <summary>Restaura o dump em uma base TEMPORÁRIA (nunca produção — Fase 14/15).</summary>
    Task RestaurarEmBaseTemporariaAsync(string nomeBaseTemporaria, Stream origem, CancellationToken cancellationToken = default);
}

/// <summary>
/// Armazenamento de ARTEFATOS de backup. Conceitualmente em um FAILURE DOMAIN separado do banco
/// (Fase 25): idealmente storage/offsite distinto do servidor de DB. A referência é lógica (rótulo +
/// nome), nunca um caminho com credencial.
/// </summary>
public interface IArmazenamentoBackup
{
    /// <summary>Grava o artefato e devolve o LOCAL lógico onde ficou (para o catálogo de backups).</summary>
    Task<string> GravarAsync(string referenciaLogica, Stream conteudo, CancellationToken cancellationToken = default);

    /// <summary>Abre o artefato para leitura (restore/verificação). Fail-closed se ausente.</summary>
    Task<Stream> AbrirAsync(string referenciaLogica, CancellationToken cancellationToken = default);

    Task<bool> ExisteAsync(string referenciaLogica, CancellationToken cancellationToken = default);

    /// <summary>Remove um artefato — usado apenas pela retenção, jamais o único backup conhecido (Fase 9).</summary>
    Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default);
}

/// <summary>
/// CÓPIA EXTERNA (offsite) dos artefatos de backup — outro disco ou servidor, fora do failure domain do primário
/// (BKP-003, auditoria de 08/10/2026). A cópia é CONFERIDA pelo hash SHA-256 do artefato depois de gravada; se não
/// conferir, é apagada e a operação falha (nunca fica uma cópia divergente tida como boa).
/// </summary>
public interface ICopiaExternaBackup
{
    /// <summary>Há destino externo configurado neste servidor (<c>ArmazenamentoBackup:DiretorioOffsite</c>)?</summary>
    bool Configurada { get; }

    /// <summary>Grava o artefato no destino externo e confere o hash da cópia.</summary>
    /// <exception cref="InvalidDataException">A cópia gravada não confere com <paramref name="hashSha256Esperado"/>.</exception>
    Task CopiarAsync(string referenciaLogica, Stream artefato, string hashSha256Esperado, CancellationToken cancellationToken = default);

    /// <summary>Remove a cópia externa (retenção). Ausente: nada a fazer.</summary>
    Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default);
}

/// <summary>
/// Empacota num ZIP os ARQUIVOS de um contribuinte — planilhas enviadas e payloads oficiais de retorno — para o backup
/// do tipo <see cref="TipoBackup.Storage"/> (BKP-005). Só a área privada de arquivos daquele contribuinte; nunca o
/// cofre nem arquivos de outro contribuinte.
/// </summary>
public interface IEmpacotadorArquivosTenant
{
    Task EmpacotarAsync(Guid tenantId, Stream destino, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cifra/decifra o artefato de backup em repouso (Fase 11), com AES-256-GCM e chave RESOLVIDA DO COFRE
/// por referência — a chave NUNCA é guardada junto ao backup (regra 7/11). Não é criptografia própria:
/// é o AEAD do framework. <see cref="ChaveRef"/> é apenas o NOME da chave (para o catálogo), nunca o valor.
/// </summary>
public interface ICriptografiaBackup
{
    /// <summary>Referência (nome no cofre) da chave usada — para registro; nunca o valor da chave.</summary>
    string ChaveRef { get; }

    Task CriptografarAsync(Stream claro, Stream cifradoDestino, CancellationToken cancellationToken = default);

    Task DecriptografarAsync(Stream cifrado, Stream claroDestino, CancellationToken cancellationToken = default);
}

/// <summary>
/// Operações de BASE usadas pela restauração aplicada (credencial administrativa do MySQL, nomes validados no
/// servidor). <see cref="BloquearAsync"/> impede duas operações simultâneas no mesmo alvo (bloqueio no banco, vale
/// entre instâncias). <see cref="TrocarTabelasAsync"/> faz a troca ATÔMICA: as tabelas da base de produção vão para a
/// base preservada (criada aqui) e as da base restaurada entram na produção — num único comando; se falhar, nada muda.
/// </summary>
public interface IOperacoesBaseRestauracao
{
    /// <exception cref="OperacaoBackupEmAndamentoException">O alvo já está bloqueado por outra operação.</exception>
    Task<IAsyncDisposable> BloquearAsync(string chave, CancellationToken cancellationToken = default);

    Task TrocarTabelasAsync(string baseProducao, string baseRestaurada, string basePreservada, CancellationToken cancellationToken = default);
}

/// <summary>
/// CATÁLOGO DE BACKUPS (Fase 13) — persistência dos metadados no banco de CATÁLOGO. Nunca guarda
/// segredo. Query por tenant/tipo para a retenção (nunca apagar o único backup — Fase 9).
/// </summary>
public interface IRepositorioRegistroBackup
{
    Task AdicionarAsync(RegistroBackup registro, CancellationToken cancellationToken = default);

    Task AtualizarAsync(RegistroBackup registro, CancellationToken cancellationToken = default);

    Task<RegistroBackup?> ObterAsync(Guid backupId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegistroBackup>> ListarPorTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegistroBackup>> ListarConcluidosAsync(CancellationToken cancellationToken = default);

    /// <summary>Quantos backups CONCLUÍDOS (não corrompidos/expirados) existem para o alvo — guarda da retenção.</summary>
    Task<int> ContarConcluidosAsync(Guid? tenantId, TipoBackup tipo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Já existe backup do <paramref name="tipo"/> desde <paramref name="desdeUtc"/> que CONCLUIU, ou que ainda está
    /// em andamento e começou depois de <paramref name="emAndamentoDesdeUtc"/>? Guarda de idempotência do backup
    /// agendado (reinício do Worker, segunda instância). Um "Iniciado" antigo (processo que caiu no meio) NÃO conta —
    /// senão o dia ficaria sem backup tido como feito (JOB-001).
    /// </summary>
    Task<bool> ExisteConcluidoOuEmAndamentoDesdeAsync(
        TipoBackup tipo, DateTimeOffset desdeUtc, DateTimeOffset emAndamentoDesdeUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Backups ainda "Iniciado" que começaram antes de <paramref name="antesDeUtc"/> (interrompidos). Devolve entidades
    /// RASTREADAS: o chamador as altera e grava com <see cref="AtualizarAsync"/>.
    /// </summary>
    Task<IReadOnlyList<RegistroBackup>> ListarIniciadosAntesDeAsync(DateTimeOffset antesDeUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Backups CONCLUÍDOS com a retenção vencida em <paramref name="agoraUtc"/> (candidatos ao descarte). Entidades
    /// RASTREADAS, como em <see cref="ListarIniciadosAntesDeAsync"/>.
    /// </summary>
    Task<IReadOnlyList<RegistroBackup>> ListarConcluidosVencidosAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken = default);

    /// <summary>Os <paramref name="quantidade"/> backups mais recentes (qualquer estado), do mais novo ao mais antigo.</summary>
    Task<IReadOnlyList<RegistroBackup>> ListarRecentesAsync(int quantidade, CancellationToken cancellationToken = default);
}
