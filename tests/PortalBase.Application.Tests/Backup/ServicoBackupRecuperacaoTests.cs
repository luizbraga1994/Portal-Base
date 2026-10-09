using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Backup;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Backup;

namespace PortalBase.Application.Tests.Backup;

/// <summary>
/// Onda 2 da auditoria de 08/10/2026 (recuperação de desastres) — testes permanentes (regra 37):
/// <list type="bullet">
/// <item>JOB-001: backup cancelado/tempo esgotado/processo que caiu nunca fica "Iniciado" (o dia não conta como feito).</item>
/// <item>BKP-004: a retenção descarta os vencidos (primário e cópia externa), nunca o último de um alvo.</item>
/// <item>BKP-003: a cópia externa é conferida pelo hash; a falha dela fica registrada sem derrubar o backup.</item>
/// <item>BKP-005: os arquivos de cada contribuinte também têm backup, um artefato por contribuinte.</item>
/// </list>
/// </summary>
public sealed class ServicoBackupRecuperacaoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 8, 3, 0, 0, TimeSpan.Zero);

    private sealed class Ambiente
    {
        public ProvedorAlvosBackupFake Alvos { get; } = new();
        public ExecutorBackupFake Executor { get; } = new();
        public ArmazenamentoBackupMemoria Store { get; } = new();
        public RepositorioRegistroBackupMemoria Repo { get; } = new();
        public AutorizacaoPlataformaFake Autz { get; } = new();
        public AuditoriaFake Auditoria { get; } = new();
        public OpcoesBackup Opcoes { get; } = new() { CriptografarEmRepouso = true, ChaveCriptografiaRef = "ref", RetencaoDiariaDias = 7 };
        public AreaTemporariaMemoria Temporarios { get; } = new();
        public CopiaExternaMemoria CopiaExterna { get; } = new();
        public EmpacotadorArquivosFake Empacotador { get; } = new();
        public RelogioAjustavel Relogio { get; } = new(Agora);

        public ServicoBackup Servico() => new(
            Alvos, Executor, Store, new CriptografiaBackupIdentidade(), Repo, new DetectorMigracaoFake(),
            new PoliticaRetencaoBackup(Opcoes), Autz, Auditoria, new ContextoSegurancaFake(), Relogio, Opcoes,
            Temporarios, CopiaExterna, Empacotador);

        public IEnumerable<string> Operacoes(Guid backupId)
            => Auditoria.Eventos.Where(e => e.RecursoId == backupId.ToString()).Select(e => e.Operacao);
    }

    // ---- JOB-001: nunca "Iniciado" para sempre ----

    [Fact]
    public async Task Cancelamento_No_Meio_Do_Dump_Marca_Falhou_E_Audita()
    {
        var amb = new Ambiente();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var acao = async () => await amb.Servico().ExecutarBackupCatalogoAsync(PeriodicidadeBackup.Diario, "portalbase_catalogo", cts.Token);

        await acao.Should().ThrowAsync<OperationCanceledException>();
        var registro = amb.Repo.Registros.Values.Should().ContainSingle().Subject;
        registro.Status.Should().Be(StatusBackup.Falhou);
        registro.UltimoErro.Should().Be("Cancelado");
        amb.Operacoes(registro.Id).Should().Contain(AcaoAuditoria.BackupFailed);
        amb.Store.Artefatos.Should().BeEmpty();
    }

    [Fact]
    public async Task Tempo_Esgotado_Do_Executor_Marca_Falhou_Com_Motivo_Proprio()
    {
        var amb = new Ambiente();
        amb.Executor.EsgotarTempo = true;

        var acao = async () => await amb.Servico().ExecutarBackupCatalogoAsync(PeriodicidadeBackup.Diario, "portalbase_catalogo");

        await acao.Should().ThrowAsync<OperationCanceledException>();
        amb.Repo.Registros.Values.Should().ContainSingle().Which.UltimoErro.Should().Be("TempoEsgotado");
    }

    [Fact]
    public async Task Varredura_Marca_Como_Falho_So_O_Iniciado_Mais_Velho_Que_O_Limite()
    {
        var amb = new Ambiente();
        var caiu = RegistroBackup.Iniciar(TipoBackup.TenantCompleto, Guid.NewGuid(), "portalbase_tenant_a", Agora - ServicoBackup.LimiteEmAndamento - TimeSpan.FromMinutes(1));
        var trabalhando = RegistroBackup.Iniciar(TipoBackup.TenantCompleto, Guid.NewGuid(), "portalbase_tenant_b", Agora - TimeSpan.FromMinutes(30));
        await amb.Repo.AdicionarAsync(caiu);
        await amb.Repo.AdicionarAsync(trabalhando);

        var marcados = await amb.Servico().MarcarInterrompidosAsync();

        marcados.Should().Be(1);
        caiu.Status.Should().Be(StatusBackup.Falhou);
        caiu.UltimoErro.Should().Be("Interrompido");
        trabalhando.Status.Should().Be(StatusBackup.Iniciado, "pode ser outra instância trabalhando agora");
        amb.Operacoes(caiu.Id).Should().ContainSingle().Which.Should().Be(AcaoAuditoria.BackupFailed);
    }

    // ---- BKP-004: retenção ----

    private static async Task<List<Guid>> TresBackupsDiariosAsync(Ambiente amb, Guid tenant)
    {
        var ids = new List<Guid>();
        for (var dia = 0; dia < 3; dia++)
        {
            amb.Relogio.AgoraUtc = Agora.AddDays(dia);
            ids.Add((await amb.Servico().ExecutarBackupTenantAsync(tenant, PeriodicidadeBackup.Diario)).BackupId);
        }

        return ids;
    }

    [Fact]
    public async Task Retencao_Descarta_Os_Vencidos_Mas_Nunca_O_Ultimo_Do_Alvo()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(b));
        var deA = await TresBackupsDiariosAsync(amb, a);
        var unicoDeB = (await amb.Servico().ExecutarBackupTenantAsync(b, PeriodicidadeBackup.Diario)).BackupId;

        amb.Relogio.AgoraUtc = Agora.AddDays(60); // todos vencidos
        var descartados = await amb.Servico().AplicarRetencaoAsync();

        descartados.Should().Be(2);
        amb.Repo.Registros[deA[0]].Status.Should().Be(StatusBackup.Expirado);
        amb.Repo.Registros[deA[1]].Status.Should().Be(StatusBackup.Expirado);
        amb.Repo.Registros[deA[2]].Status.Should().Be(StatusBackup.Concluido, "o mais recente de A fica, mesmo vencido");
        amb.Repo.Registros[unicoDeB].Status.Should().Be(StatusBackup.Concluido, "o único backup de B nunca é apagado");
        amb.Store.Artefatos.Keys.Should().BeEquivalentTo(
            [amb.Repo.Registros[deA[2]].LocalArmazenamentoLogico, amb.Repo.Registros[unicoDeB].LocalArmazenamentoLogico]);
        amb.Operacoes(deA[0]).Should().Contain(AcaoAuditoria.BackupPruned);

        (await amb.Servico().AplicarRetencaoAsync()).Should().Be(0, "repetir não apaga mais nada (idempotente)");
    }

    [Fact]
    public async Task Retencao_Nao_Descarta_O_Que_Ainda_Esta_Na_Janela()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));
        await TresBackupsDiariosAsync(amb, a);

        amb.Relogio.AgoraUtc = Agora.AddDays(7).AddHours(12); // só o do dia 0 venceu (7 dias)
        (await amb.Servico().AplicarRetencaoAsync()).Should().Be(1);
        amb.Repo.Registros.Values.Count(r => r.Status == StatusBackup.Concluido).Should().Be(2);
    }

    [Fact]
    public async Task Retencao_Apaga_Tambem_A_Copia_Externa()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));
        var ids = await TresBackupsDiariosAsync(amb, a);
        amb.CopiaExterna.Copias.Should().HaveCount(3);

        amb.Relogio.AgoraUtc = Agora.AddDays(60);
        await amb.Servico().AplicarRetencaoAsync();

        amb.CopiaExterna.Copias.Keys.Should().ContainSingle().Which.Should().Be(amb.Repo.Registros[ids[2]].LocalArmazenamentoLogico);
    }

    [Fact]
    public async Task Retencao_Que_Nao_Consegue_Apagar_Mantem_Concluido_Para_Tentar_De_Novo()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));
        var ids = await TresBackupsDiariosAsync(amb, a);
        amb.Store.FalharRemocao = true;

        amb.Relogio.AgoraUtc = Agora.AddDays(60);
        (await amb.Servico().AplicarRetencaoAsync()).Should().Be(0);

        ids.Select(id => amb.Repo.Registros[id].Status).Should().OnlyContain(s => s == StatusBackup.Concluido,
            "registro Expirado com o arquivo ainda no disco seria um artefato órfão sem controle");
        amb.Store.FalharRemocao = false;
        (await amb.Servico().AplicarRetencaoAsync()).Should().Be(2);
    }

    // ---- BKP-003: cópia externa ----

    [Fact]
    public async Task Copia_Externa_Conferida_Fica_Registrada_E_Igual_Ao_Primario()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));

        var r = await amb.Servico().ExecutarBackupTenantAsync(a, PeriodicidadeBackup.Diario);

        r.CopiaExterna.Should().Be(SituacaoCopiaExterna.Concluida);
        var registro = amb.Repo.Registros[r.BackupId];
        registro.CopiaExterna.Should().Be(SituacaoCopiaExterna.Concluida);
        registro.CopiaExternaEmUtc.Should().Be(Agora);
        amb.CopiaExterna.Copias[registro.LocalArmazenamentoLogico!].Should().Equal(amb.Store.Artefatos[registro.LocalArmazenamentoLogico!]);
        amb.Operacoes(r.BackupId).Should().Contain(AcaoAuditoria.BackupCopiedOffsite);
    }

    [Fact]
    public async Task Falha_Da_Copia_Externa_Nao_Derruba_O_Backup_Mas_Fica_Registrada()
    {
        var amb = new Ambiente();
        amb.CopiaExterna.Falhar = true;
        var a = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));

        var r = await amb.Servico().ExecutarBackupTenantAsync(a, PeriodicidadeBackup.Diario);

        r.Status.Should().Be(StatusBackup.Concluido, "o backup primário vale");
        r.CopiaExterna.Should().Be(SituacaoCopiaExterna.Falhou);
        amb.Repo.Registros[r.BackupId].CopiaExterna.Should().Be(SituacaoCopiaExterna.Falhou);
        amb.Operacoes(r.BackupId).Should().Contain(AcaoAuditoria.BackupOffsiteFailed);
    }

    [Fact]
    public async Task Sem_Copia_Externa_Configurada_A_Situacao_Fica_Vazia()
    {
        var amb = new Ambiente();
        amb.CopiaExterna.Configurada = false;

        var r = await amb.Servico().ExecutarBackupCatalogoAsync(PeriodicidadeBackup.Diario, "portalbase_catalogo");

        r.CopiaExterna.Should().BeNull();
        amb.Repo.Registros[r.BackupId].CopiaExterna.Should().BeNull();
        amb.CopiaExterna.Copias.Should().BeEmpty();
    }

    // ---- BKP-005: arquivos dos contribuintes ----

    [Fact]
    public async Task Arquivos_Tem_Um_Backup_Por_Contribuinte_Provisionado_E_Isolado()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(b));
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionando(Guid.NewGuid()));

        var resultados = await amb.Servico().ExecutarBackupArquivosTenantsAsync(PeriodicidadeBackup.Diario);

        resultados.Should().HaveCount(2).And.OnlyContain(r => r.Status == StatusBackup.Concluido && r.Tipo == TipoBackup.Storage);
        amb.Empacotador.Empacotados.Should().BeEquivalentTo([a, b]);
        foreach (var r in resultados)
        {
            var registro = amb.Repo.Registros[r.BackupId];
            registro.TenantId.Should().Be(r.TenantId);
            registro.Criptografado.Should().BeTrue();
            registro.LocalArmazenamentoLogico.Should().Contain(r.TenantId!.Value.ToString("N"), "o artefato fica na pasta do próprio contribuinte");
            System.Text.Encoding.UTF8.GetString(amb.Store.Artefatos[registro.LocalArmazenamentoLogico!])
                .Should().Be($"ZIP::{r.TenantId:N}", "cada pacote só tem os arquivos do seu contribuinte");
        }
    }

    [Fact]
    public async Task Falha_Ao_Empacotar_Fica_Registrada_Sem_Artefato()
    {
        var amb = new Ambiente();
        amb.Empacotador.Falhar = true;
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(Guid.NewGuid()));

        var resultados = await amb.Servico().ExecutarBackupArquivosTenantsAsync(PeriodicidadeBackup.Diario);

        resultados.Should().ContainSingle().Which.Status.Should().Be(StatusBackup.Falhou);
        amb.Repo.Registros.Values.Should().ContainSingle().Which.UltimoErro.Should().Be("IOException", "só o tipo do erro, sem caminho");
        amb.Store.Artefatos.Should().BeEmpty();
    }

    [Fact]
    public async Task O_Conteudo_Passa_Pela_Area_Temporaria_E_Nao_Pela_Memoria()
    {
        var amb = new Ambiente();

        await amb.Servico().ExecutarBackupCatalogoAsync(PeriodicidadeBackup.Diario, "portalbase_catalogo");

        amb.Temporarios.Criados.Should().Be(2, "um arquivo temporário para o dump e outro para o artefato cifrado");
    }
}
