using FluentAssertions;
using PortalBase.Application.Backup;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Backup;

namespace PortalBase.Application.Tests.Backup;

/// <summary>
/// Backup AGENDADO: calendário (janela diária no horário UTC; periodicidade pelo dia — dia 1 mensal, domingo semanal)
/// e execução da janela (catálogo + contribuintes; idempotente por janela; executor fora/migração ⇒ bloqueado, sem
/// fingir sucesso; base do catálogo ausente ⇒ não executa).
/// </summary>
public sealed class BackupAgendadoTests
{
    private static readonly TimeOnly Tres = new(3, 0);

    // ---- Calendário ----

    [Theory]
    [InlineData("03:00", true)]
    [InlineData(" 23:59 ", true)]
    [InlineData("3:00", false)]
    [InlineData("24:00", false)]
    [InlineData("03h00", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Horario_So_Aceita_HH_mm(string? texto, bool valido)
        => AgendaBackup.TentarLerHorario(texto, out _).Should().Be(valido);

    [Fact]
    public void Janela_Vigente_E_A_De_Hoje_Depois_Do_Horario_E_A_De_Ontem_Antes()
    {
        var depois = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        AgendaBackup.InicioJanela(Tres, depois).Should().Be(depois);
        AgendaBackup.ProximaJanela(Tres, depois).Should().Be(depois.AddDays(1));

        var antes = new DateTimeOffset(2026, 10, 7, 2, 59, 59, TimeSpan.Zero);
        AgendaBackup.InicioJanela(Tres, antes).Should().Be(new DateTimeOffset(2026, 10, 6, 3, 0, 0, TimeSpan.Zero));

        // Horário local com fuso: convertido para UTC antes de decidir (00:30 em -03:00 = 03:30 UTC).
        var comFuso = new DateTimeOffset(2026, 10, 7, 0, 30, 0, TimeSpan.FromHours(-3));
        AgendaBackup.InicioJanela(Tres, comFuso).Should().Be(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Periodicidade_Dia_1_Mensal_Domingo_Semanal_Demais_Diario()
    {
        AgendaBackup.Periodicidade(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero)).Should().Be(PeriodicidadeBackup.Mensal, "dia 1, mesmo sendo domingo");
        AgendaBackup.Periodicidade(new DateTimeOffset(2026, 10, 4, 3, 0, 0, TimeSpan.Zero)).Should().Be(PeriodicidadeBackup.Semanal);
        AgendaBackup.Periodicidade(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero)).Should().Be(PeriodicidadeBackup.Diario);
    }

    // ---- Execução da janela ----

    private sealed class Ambiente
    {
        public static readonly DateTimeOffset Janela = new(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);

        public ProvedorAlvosBackupFake Alvos { get; } = new();
        public ExecutorBackupFake Executor { get; } = new();
        public RepositorioRegistroBackupMemoria Repo { get; } = new();
        public DetectorMigracaoFake Migracao { get; } = new();
        public AutorizacaoPlataformaFake Autz { get; } = new();
        public OpcoesBackup Opcoes { get; } = new() { CriptografarEmRepouso = true, ChaveCriptografiaRef = "ref" };
        public ArmazenamentoBackupMemoria Store { get; } = new();
        public CopiaExternaMemoria CopiaExterna { get; } = new();
        public EmpacotadorArquivosFake Empacotador { get; } = new();
        public AuditoriaFake Auditoria { get; } = new();
        public RelogioAjustavel Relogio { get; } = new(Janela.AddMinutes(5));

        public ExecucaoBackupAgendado Criar()
        {
            var servico = new ServicoBackup(
                Alvos, Executor, Store, new CriptografiaBackupIdentidade(), Repo, Migracao,
                new PoliticaRetencaoBackup(Opcoes), Autz, Auditoria, new ContextoSegurancaFake(), Relogio, Opcoes,
                new AreaTemporariaMemoria(), CopiaExterna, Empacotador);
            return new ExecucaoBackupAgendado(servico, Repo, Alvos, Opcoes, Relogio);
        }
    }

    [Fact]
    public async Task Executa_Catalogo_E_Contribuintes_E_Nao_Repete_A_Mesma_Janela()
    {
        var amb = new Ambiente();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(Guid.NewGuid()));
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(Guid.NewGuid()));
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionando(Guid.NewGuid()));

        var r = await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela);

        r.Desfecho.Should().Be(DesfechoBackupAgendado.Executado);
        r.Periodicidade.Should().Be(PeriodicidadeBackup.Diario);
        r.TenantsConcluidos.Should().Be(2);
        r.TenantsComFalha.Should().Be(0);
        r.ArquivosConcluidos.Should().Be(2, "os arquivos de cada contribuinte provisionado também entram (BKP-005)");
        r.ArquivosComFalha.Should().Be(0);
        amb.Repo.Registros.Values.Should().ContainSingle(x => x.Tipo == TipoBackup.CatalogoCompleto && x.AlvoLogico == "portalbase_catalogo");
        amb.Repo.Registros.Values.Count(x => x.Tipo == TipoBackup.Storage).Should().Be(2);
        amb.Repo.Registros.Should().HaveCount(5);

        var repetido = await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela);
        repetido.Desfecho.Should().Be(DesfechoBackupAgendado.JaExecutado, "reinício do Worker ou segunda instância não duplicam a janela");
        amb.Repo.Registros.Should().HaveCount(5);
    }

    [Fact]
    public async Task Executor_Fisico_Indisponivel_E_Bloqueado_Sem_Registro_De_Sucesso()
    {
        var amb = new Ambiente();
        amb.Executor.Disponivel = false;
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(Guid.NewGuid()));

        var r = await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela);

        r.Desfecho.Should().Be(DesfechoBackupAgendado.Bloqueado);
        r.Motivo.Should().Contain("Executor");
        amb.Repo.Registros.Should().BeEmpty();
        (await amb.Criar().JanelaAtendidaAsync(Ambiente.Janela)).Should().BeFalse("a janela continua pendente para nova tentativa");
    }

    [Fact]
    public async Task Migracao_Em_Andamento_Bloqueia_E_Falha_Do_Dump_Nao_Atende_A_Janela()
    {
        var amb = new Ambiente();
        amb.Migracao.EmAndamento = true;
        (await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela)).Desfecho.Should().Be(DesfechoBackupAgendado.Bloqueado);

        amb.Migracao.EmAndamento = false;
        amb.Executor.FalharDump = true;
        var falha = await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela);
        falha.Desfecho.Should().Be(DesfechoBackupAgendado.Falhou);
        falha.Motivo.Should().NotContain("portalbase", "o motivo é só o tipo do erro, sem nome de base/caminho");
        (await amb.Criar().JanelaAtendidaAsync(Ambiente.Janela)).Should().BeFalse("backup que falhou não conta como atendido");
    }

    [Fact]
    public async Task Sem_Base_Do_Catalogo_Nao_Executa_E_Opcao_Explicita_Prevalece()
    {
        var amb = new Ambiente();
        amb.Alvos.NomeBaseCatalogo = null;
        (await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela)).Desfecho.Should().Be(DesfechoBackupAgendado.Bloqueado);
        amb.Repo.Registros.Should().BeEmpty();

        amb.Opcoes.NomeBaseCatalogo = "catalogo_explicito";
        (await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela)).Desfecho.Should().Be(DesfechoBackupAgendado.Executado);
        amb.Repo.Registros.Values.Should().ContainSingle(x => x.AlvoLogico == "catalogo_explicito");
    }

    [Fact]
    public async Task Iniciado_De_Processo_Que_Caiu_Nao_Atende_A_Janela_E_Vira_Falho()
    {
        // JOB-001 (auditoria de 08/10/2026): antes, um registro "Iniciado" de um Worker que caiu contava como janela
        // atendida e o dia ficava sem backup.
        var amb = new Ambiente();
        var caiu = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "portalbase_catalogo", Ambiente.Janela.AddMinutes(1));
        await amb.Repo.AdicionarAsync(caiu);

        (await amb.Criar().JanelaAtendidaAsync(Ambiente.Janela)).Should().BeTrue("5 minutos depois ainda pode estar rodando");

        amb.Relogio.AgoraUtc = Ambiente.Janela.Add(ServicoBackup.LimiteEmAndamento).AddMinutes(2);
        (await amb.Criar().JanelaAtendidaAsync(Ambiente.Janela)).Should().BeFalse("passado o limite, é um processo que caiu");

        var r = await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela);

        r.Desfecho.Should().Be(DesfechoBackupAgendado.Executado);
        r.Interrompidos.Should().Be(1);
        caiu.Status.Should().Be(StatusBackup.Falhou);
        amb.Repo.Registros.Values.Should().Contain(x => x.Tipo == TipoBackup.CatalogoCompleto && x.Status == StatusBackup.Concluido);
    }

    [Fact]
    public async Task Janela_Conta_Copias_Externas_Com_Falha_E_Aplica_A_Retencao()
    {
        var amb = new Ambiente();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(Guid.NewGuid()));
        amb.CopiaExterna.Falhar = true;

        var r = await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela);
        r.CopiasExternasComFalha.Should().Be(3, "catálogo, base e arquivos do contribuinte: nenhuma falha de cópia passa calada");

        // 60 dias depois, com a cópia externa de volta: os do primeiro dia vencem, mas são os últimos de cada alvo.
        amb.CopiaExterna.Falhar = false;
        amb.Relogio.AgoraUtc = Ambiente.Janela.AddDays(60).AddMinutes(5);
        var depois = await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela.AddDays(60));
        depois.Desfecho.Should().Be(DesfechoBackupAgendado.Executado);
        depois.CopiasExternasComFalha.Should().Be(0);
        depois.Descartados.Should().Be(3, "os 3 do primeiro dia saem porque agora há um backup mais novo de cada alvo");
        amb.Repo.Registros.Values.Count(x => x.Status == StatusBackup.Expirado).Should().Be(3);
    }

    [Fact]
    public async Task Sem_Permissao_De_Plataforma_Falha_Sem_Executar()
    {
        var amb = new Ambiente();
        amb.Autz.Permite = false;

        var r = await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela);

        r.Desfecho.Should().Be(DesfechoBackupAgendado.Falhou);
        amb.Repo.Registros.Should().BeEmpty();
    }

    // ---- Consulta autorizada (tela Operações → Backups) ----

    private sealed class ContribuintesFixos(params (Guid Id, string Nome)[] itens) : PortalBase.Application.Catalogo.IConsultaContribuintesPlataforma
    {
        public int Chamadas { get; private set; }

        public Task<IReadOnlyList<PortalBase.Application.Catalogo.ResumoContribuinte>> ListarAsync(int maximo, CancellationToken cancellationToken = default)
        {
            Chamadas++;
            return Task.FromResult<IReadOnlyList<PortalBase.Application.Catalogo.ResumoContribuinte>>(itens
                .Select(i => new PortalBase.Application.Catalogo.ResumoContribuinte(i.Id, i.Nome, "00000000000191", i.Nome, PortalBase.Domain.Catalogo.EstadoProvisionamento.Ativo, false, DateTimeOffset.UnixEpoch))
                .ToList());
        }
    }

    [Fact]
    public async Task Consulta_De_Backups_Exige_Permissao_E_Mostra_O_Nome_Do_Contribuinte()
    {
        var amb = new Ambiente();
        var tenant = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(tenant));
        await amb.Criar().ExecutarJanelaAsync(Ambiente.Janela);
        var contribuintes = new ContribuintesFixos((tenant, "Empresa A"));

        var itens = await new ConsultaBackupsAutorizada(amb.Autz, amb.Repo, contribuintes).ListarRecentesAsync(500);

        itens.Should().HaveCount(3);
        itens.Single(i => i.Tipo == TipoBackup.TenantCompleto).NomeContribuinte.Should().Be("Empresa A");
        itens.Single(i => i.Tipo == TipoBackup.Storage).NomeContribuinte.Should().Be("Empresa A");
        itens.Single(i => i.Tipo == TipoBackup.CatalogoCompleto).NomeContribuinte.Should().BeNull();

        amb.Autz.Permite = false;
        var negar = async () => await new ConsultaBackupsAutorizada(amb.Autz, amb.Repo, contribuintes).ListarRecentesAsync();
        await negar.Should().ThrowAsync<PortalBase.Application.Excecoes.AutorizacaoNegadaException>();
        contribuintes.Chamadas.Should().Be(1, "sem permissão nada é lido");
    }
}
