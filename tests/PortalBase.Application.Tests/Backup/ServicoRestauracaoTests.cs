using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Backup;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Backup;

namespace PortalBase.Application.Tests.Backup;

/// <summary>
/// Prompt 50 — RESTAURAÇÃO (Fases 14-16, 26, 28-30). Prova que BACKUP ≠ RESTORE: rejeita cross-tenant
/// (Fase 29), nunca sobrescreve produção (Fase 15), detecta corrupção por hash antes de restaurar
/// (Fase 26), recusa backup incompleto (Fase 27), exige autorização de plataforma (Fase 30) e é BLOCKED
/// quando o executor físico está indisponível (Fase 33). Tudo auditado, sem segredo.
/// </summary>
public sealed class ServicoRestauracaoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class Ambiente
    {
        public ProvedorAlvosBackupFake Alvos { get; } = new();
        public ExecutorBackupFake Executor { get; } = new();
        public ArmazenamentoBackupMemoria Store { get; } = new();
        public CriptografiaBackupIdentidade Cripto { get; } = new();
        public RepositorioRegistroBackupMemoria Repo { get; } = new();
        public DetectorMigracaoFake Migracao { get; } = new();
        public AutorizacaoPlataformaFake Autz { get; } = new();
        public AuditoriaFake Auditoria { get; } = new();
        public ContextoSegurancaFake Seguranca { get; } = new();
        public OpcoesBackup Opcoes { get; } = new() { CriptografarEmRepouso = true, ChaveCriptografiaRef = "ref" };

        public ServicoBackup ServicoBackup() => new(
            Alvos, Executor, Store, Cripto, Repo, Migracao,
            new PoliticaRetencaoBackup(Opcoes), Autz, Auditoria, Seguranca, new RelogioFixo(Agora), Opcoes,
            new AreaTemporariaMemoria(), new CopiaExternaMemoria { Configurada = false }, new EmpacotadorArquivosFake());

        public ServicoRestauracao ServicoRestauracao() => new(
            Repo, Store, Cripto, Executor, Alvos, Autz, Auditoria, Seguranca, new RelogioFixo(Agora));

        public async Task<(Guid BackupId, RegistroBackup Registro)> CriarBackupTenantAsync(Guid tenantId)
        {
            Alvos.Alvos.Add(AlvosBackup.Provisionado(tenantId));
            var resultados = await ServicoBackup().ExecutarBackupTenantsAsync(PeriodicidadeBackup.Diario);
            var id = resultados.Single().BackupId;
            return (id, Repo.Registros[id]);
        }
    }

    [Fact] // Fase 29 — backup de A NÃO restaura em B (rejeitado + auditado)
    public async Task Restore_CrossTenant_Rejeitado()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (backupId, _) = await amb.CriarBackupTenantAsync(a);

        var acao = async () => await amb.ServicoRestauracao().RestaurarTenantParaTemporarioAsync(backupId, b, $"restore_{b:N}");

        await acao.Should().ThrowAsync<RestauracaoCrossTenantException>();
        amb.Auditoria.Eventos.Select(e => e.Operacao).Should().Contain(AcaoAuditoria.RestoreCrossTenantDenied);
    }

    [Fact] // Fase 15 — nunca restaura sobre a base de PRODUÇÃO (só base temporária)
    public async Task Restore_Em_Producao_Rejeitado()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var (backupId, registro) = await amb.CriarBackupTenantAsync(a);
        var baseViva = amb.Alvos.Alvos.Single(x => x.TenantId == a).NomeBaseFisica;

        var acao = async () => await amb.ServicoRestauracao().RestaurarTenantParaTemporarioAsync(backupId, a, baseViva);

        await acao.Should().ThrowAsync<RestauracaoEmProducaoException>();
        registro.RestoreTestado.Should().BeFalse();
    }

    [Fact] // Fase 26 — hash divergente ⇒ corrupção detectada, restore recusado, artefato marcado corrompido
    public async Task Restore_Hash_Divergente_Detecta_Corrupcao()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var (backupId, registro) = await amb.CriarBackupTenantAsync(a);

        // adultera o artefato no store (corrupção)
        var chave = registro.LocalArmazenamentoLogico!;
        amb.Store.Artefatos[chave][0] ^= 0xFF;

        var acao = async () => await amb.ServicoRestauracao().RestaurarTenantParaTemporarioAsync(backupId, a, $"restore_{a:N}");

        await acao.Should().ThrowAsync<BackupCorrompidoException>();
        amb.Repo.Registros[backupId].Status.Should().Be(StatusBackup.Corrompido);
        amb.Auditoria.Eventos.Select(e => e.Operacao).Should().Contain(AcaoAuditoria.BackupCorrupted);
        amb.Executor.BasesRestauradas.Should().BeEmpty(); // nunca restaurou
    }

    [Fact] // Fase 27 — backup incompleto (não concluído) é recusado antes de qualquer restauração
    public async Task Restore_Backup_Incompleto_Rejeitado()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var incompleto = RegistroBackup.Iniciar(TipoBackup.TenantCompleto, a, $"tenant_{a:N}", Agora);
        await amb.Repo.AdicionarAsync(incompleto);

        var acao = async () => await amb.ServicoRestauracao().RestaurarTenantParaTemporarioAsync(incompleto.Id, a, $"restore_{a:N}");

        await acao.Should().ThrowAsync<BackupInvalidoException>();
    }

    [Fact] // Fase 14-16 — restore válido em base temporária: sucesso registrado como TESTE de restauração
    public async Task Restore_Valido_Registra_Teste_De_Restauracao()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var (backupId, _) = await amb.CriarBackupTenantAsync(a);
        var baseTemp = $"restore_{a:N}";

        var resultado = await amb.ServicoRestauracao().RestaurarTenantParaTemporarioAsync(backupId, a, baseTemp);

        resultado.Restaurado.Should().BeTrue();
        resultado.IntegridadeOk.Should().BeTrue();
        amb.Executor.BasesRestauradas.Should().Contain(baseTemp);
        amb.Repo.Registros[backupId].RestoreTestado.Should().BeTrue();
        amb.Auditoria.Eventos.Select(e => e.Operacao).Should().Contain(AcaoAuditoria.RestoreCompleted);
    }

    [Fact] // Fase 30 — restore sem autorização de plataforma é negado (deny-by-default)
    public async Task Restore_Nao_Autorizado_Negado()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var (backupId, _) = await amb.CriarBackupTenantAsync(a);
        amb.Autz.Permite = false;

        var acao = async () => await amb.ServicoRestauracao().RestaurarTenantParaTemporarioAsync(backupId, a, $"restore_{a:N}");

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [Fact] // Fase 33 — restore físico BLOCKED quando o executor está indisponível (nunca simulado)
    public async Task Restore_Executor_Indisponivel_Bloqueado()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var (backupId, _) = await amb.CriarBackupTenantAsync(a);
        amb.Executor.Disponivel = false;

        var acao = async () => await amb.ServicoRestauracao().RestaurarTenantParaTemporarioAsync(backupId, a, $"restore_{a:N}");

        await acao.Should().ThrowAsync<ExecutorBackupIndisponivelException>();
    }

    [Fact] // Fase 26 — verificação de integridade independente detecta corrupção e marca o artefato
    public async Task VerificarIntegridade_Detecta_Corrupcao()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var (backupId, registro) = await amb.CriarBackupTenantAsync(a);

        (await amb.ServicoRestauracao().VerificarIntegridadeAsync(backupId)).Should().BeTrue();

        amb.Store.Artefatos[registro.LocalArmazenamentoLogico!][0] ^= 0xFF;
        (await amb.ServicoRestauracao().VerificarIntegridadeAsync(backupId)).Should().BeFalse();
        amb.Repo.Registros[backupId].Status.Should().Be(StatusBackup.Corrompido);
    }
}
