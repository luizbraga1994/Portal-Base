using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Backup;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Backup;

namespace PortalBase.Application.Tests.Backup;

/// <summary>
/// Prompt 50 — orquestração de BACKUP (Fases 3-13) sobre fakes (sem MySQL): isolamento A→Backup A,
/// pula tenant não provisionado (Fase 7), paralelismo limitado (Fase 6), bloqueio em migration (Fase 8),
/// autorização de plataforma (Fase 30), cifra+hash+registro no catálogo (Fases 11-13) e BLOCKED honesto
/// quando o executor físico está indisponível (Fase 33).
/// </summary>
public sealed class ServicoBackupTests
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
        public OpcoesBackup Opcoes { get; } = new() { CriptografarEmRepouso = true, ChaveCriptografiaRef = "ref", ParalelismoMaximo = 2 };
        public AreaTemporariaMemoria Temporarios { get; } = new();
        public CopiaExternaMemoria CopiaExterna { get; } = new() { Configurada = false };
        public EmpacotadorArquivosFake Empacotador { get; } = new();
        public RelogioFixo Relogio { get; } = new(Agora);

        public ServicoBackup Servico() => new(
            Alvos, Executor, Store, Cripto, Repo, Migracao,
            new PoliticaRetencaoBackup(Opcoes), Autz, Auditoria, Seguranca, Relogio, Opcoes, Temporarios, CopiaExterna, Empacotador);
    }

    [Fact] // Fase 5 — um backup por tenant provisionado, cada um com seu próprio tenant (A→Backup A)
    public async Task Backup_Tenants_Gera_Um_Backup_Por_Tenant_Provisionado()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(b));

        var resultados = await amb.Servico().ExecutarBackupTenantsAsync(PeriodicidadeBackup.Diario);

        resultados.Should().HaveCount(2);
        resultados.Should().OnlyContain(r => r.Status == StatusBackup.Concluido && r.Hash != null);
        resultados.Select(r => r.TenantId).Should().BeEquivalentTo([a, b]);
        amb.Repo.Registros.Values.Should().OnlyContain(r => r.Tipo == TipoBackup.TenantCompleto && r.CandidatoRestauracao);
        amb.Auditoria.Eventos.Select(e => e.Operacao).Should().Contain(AcaoAuditoria.BackupCompleted);
    }

    [Fact] // Fase 7 — tenant em provisionamento é PULADO (base parcial não gera backup válido)
    public async Task Backup_Ignora_Tenant_Nao_Provisionado()
    {
        var amb = new Ambiente();
        var pronto = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(pronto));
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionando(Guid.NewGuid())); // deve ser ignorado

        var resultados = await amb.Servico().ExecutarBackupTenantsAsync(PeriodicidadeBackup.Diario);

        resultados.Should().ContainSingle().Which.TenantId.Should().Be(pronto);
    }

    [Fact] // Fase 6 — o paralelismo respeita o limite configurado
    public async Task Backup_Paralelismo_Respeita_Limite()
    {
        var amb = new Ambiente();
        amb.Opcoes.ParalelismoMaximo = 2;
        for (var i = 0; i < 6; i++)
        {
            amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(Guid.NewGuid()));
        }

        await amb.Servico().ExecutarBackupTenantsAsync(PeriodicidadeBackup.Diario);

        amb.Executor.ConcorrenciaMaxima.Should().BeLessThanOrEqualTo(2);
    }

    [Fact] // Fase 33 — sem executor físico, o backup do catálogo é BLOCKED (nunca simulado como sucesso)
    public async Task Backup_Catalogo_Executor_Indisponivel_Bloqueado()
    {
        var amb = new Ambiente();
        amb.Executor.Disponivel = false;

        var acao = async () => await amb.Servico().ExecutarBackupCatalogoAsync(PeriodicidadeBackup.Mensal, "portalbase_catalogo");

        await acao.Should().ThrowAsync<ExecutorBackupIndisponivelException>();
    }

    [Fact] // Fase 8 — migration em andamento + política de bloqueio ⇒ backup recusado (artefato inconsistente evitado)
    public async Task Backup_Durante_Migracao_Bloqueado()
    {
        var amb = new Ambiente();
        amb.Migracao.EmAndamento = true;

        var acao = async () => await amb.Servico().ExecutarBackupCatalogoAsync(PeriodicidadeBackup.Diario, "portalbase_catalogo");

        await acao.Should().ThrowAsync<BackupBloqueadoPorMigracaoException>();
    }

    [Fact] // Fase 30 — sem autorização de plataforma, nada é feito (deny-by-default)
    public async Task Backup_Nao_Autorizado_Negado()
    {
        var amb = new Ambiente();
        amb.Autz.Permite = false;
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(Guid.NewGuid()));

        var acao = async () => await amb.Servico().ExecutarBackupTenantsAsync(PeriodicidadeBackup.Diario);

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        amb.Repo.Registros.Should().BeEmpty();
    }

    [Fact] // Fases 11-13 — backup criptografado registra a REFERÊNCIA da chave, nunca o valor
    public async Task Backup_Criptografado_Registra_ChaveRef_Nunca_Valor()
    {
        var amb = new Ambiente();
        var resultado = await amb.Servico().ExecutarBackupCatalogoAsync(PeriodicidadeBackup.Mensal, "portalbase_catalogo");

        var registro = amb.Repo.Registros[resultado.BackupId];
        registro.Criptografado.Should().BeTrue();
        registro.ChaveRef.Should().Be("backup-key-ref-teste");
        registro.HashSha256.Should().NotBeNullOrEmpty();
        registro.Tipo.Should().Be(TipoBackup.CatalogoCompleto);
        registro.TenantId.Should().BeNull();
        registro.RetencaoAteUtc.Should().BeAfter(Agora);
    }
}
