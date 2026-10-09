using FluentAssertions;
using PortalBase.Application.Backup;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Tests.Backup;

/// <summary>
/// Backup SOB DEMANDA pela tela da plataforma: mesmo pipeline do agendado (cifra, hash, registro, auditoria), alvo
/// resolvido no catálogo, um backup manual por vez, executor físico fora ⇒ bloqueado (nunca sucesso simulado).
/// </summary>
public sealed class BackupSobDemandaTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private sealed class BloqueioFake : IOperacoesBaseRestauracao
    {
        public bool Ocupado { get; set; }

        public List<string> Chaves { get; } = [];

        public int Liberacoes { get; private set; }

        public Task<IAsyncDisposable> BloquearAsync(string chave, CancellationToken cancellationToken = default)
        {
            if (Ocupado)
            {
                throw new OperacaoBackupEmAndamentoException();
            }

            Chaves.Add(chave);
            return Task.FromResult<IAsyncDisposable>(new Liberacao(this));
        }

        public Task TrocarTabelasAsync(string baseProducao, string baseRestaurada, string basePreservada, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("O backup sob demanda nunca troca tabelas.");

        private sealed class Liberacao(BloqueioFake dono) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                dono.Liberacoes++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Ambiente
    {
        public ProvedorAlvosBackupFake Alvos { get; } = new();
        public ExecutorBackupFake Executor { get; } = new();
        public ArmazenamentoBackupMemoria Store { get; } = new();
        public CriptografiaBackupIdentidade Cripto { get; } = new();
        public RepositorioRegistroBackupMemoria Repo { get; } = new();
        public AutorizacaoPlataformaFake Autz { get; } = new();
        public AuditoriaFake Auditoria { get; } = new();
        public BloqueioFake Bloqueio { get; } = new();
        public OpcoesBackup Opcoes { get; } = new()
        {
            CriptografarEmRepouso = true,
            ChaveCriptografiaRef = "ref",
            AgendamentoHabilitado = true,
            HorarioAgendamentoUtc = "03:00",
        };

        public CopiaExternaMemoria CopiaExterna { get; } = new();

        public EmpacotadorArquivosFake Empacotador { get; } = new();

        public BackupSobDemanda Criar() => new(
            new ServicoBackup(Alvos, Executor, Store, Cripto, Repo, new DetectorMigracaoFake(), new PoliticaRetencaoBackup(Opcoes),
                Autz, Auditoria, new ContextoSegurancaFake(), new RelogioFixo(Agora), Opcoes,
                new AreaTemporariaMemoria(), CopiaExterna, Empacotador),
            Alvos, Executor, Cripto, Bloqueio, Autz, Opcoes, CopiaExterna);
    }

    [Fact]
    public async Task Contribuinte_CopiaSoOEscolhido_ComBloqueioManual()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(b));

        var resultado = await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.Contribuinte, b, PeriodicidadeBackup.Mensal);

        resultado.Concluidos.Should().Be(2, "a base e os arquivos do contribuinte (BKP-005)");
        resultado.ComFalha.Should().Be(0);
        amb.Repo.Registros.Values.Select(r => r.Tipo).Should().BeEquivalentTo([TipoBackup.TenantCompleto, TipoBackup.Storage]);
        amb.Repo.Registros.Values.Should().OnlyContain(r => r.TenantId == b && r.Status == StatusBackup.Concluido
            && r.RetencaoAteUtc == Agora.AddDays(amb.Opcoes.RetencaoMensalDias)); // retenção da escolha (mensal)
        amb.Empacotador.Empacotados.Should().Equal(b);
        amb.Bloqueio.Chaves.Should().Equal("portalbase_backup_manual");
        amb.Bloqueio.Liberacoes.Should().Be(1);
    }

    [Fact]
    public async Task Catalogo_CopiaABaseDoCatalogo()
    {
        var amb = new Ambiente();

        var resultado = await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.Catalogo, null, PeriodicidadeBackup.Diario);

        resultado.Concluidos.Should().Be(1);
        var registro = amb.Repo.Registros.Values.Should().ContainSingle().Subject;
        registro.Tipo.Should().Be(TipoBackup.CatalogoCompleto);
        registro.AlvoLogico.Should().Be("portalbase_catalogo");
    }

    [Fact]
    public async Task Catalogo_SemBaseConhecida_Recusado()
    {
        var amb = new Ambiente();
        amb.Alvos.NomeBaseCatalogo = null;

        var acao = async () => await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.Catalogo, null, PeriodicidadeBackup.Diario);

        await acao.Should().ThrowAsync<BackupInvalidoException>();
        amb.Repo.Registros.Should().BeEmpty();
    }

    [Fact]
    public async Task Todos_CopiaSoOsProvisionados()
    {
        var amb = new Ambiente();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(Guid.NewGuid()));
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(Guid.NewGuid()));

        var resultado = await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.TodosContribuintes, null, PeriodicidadeBackup.Semanal);

        resultado.Concluidos.Should().Be(4, "base e arquivos de cada um dos dois");
        resultado.Itens.Should().HaveCount(4);
        resultado.Itens.Count(i => i.Tipo == TipoBackup.Storage).Should().Be(2);
    }

    [Fact]
    public async Task Contribuinte_Falha_Nos_Arquivos_Aparece_Sem_Esconder_A_Base_Copiada()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionado(a));
        amb.Empacotador.Falhar = true;

        var resultado = await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.Contribuinte, a, PeriodicidadeBackup.Diario);

        resultado.Concluidos.Should().Be(1);
        resultado.ComFalha.Should().Be(1);
        resultado.Itens.Single(i => i.Tipo == TipoBackup.Storage).Erro.Should().Be("IOException", "só o tipo do erro, sem caminho");
        amb.Bloqueio.Liberacoes.Should().Be(1);
    }

    [Fact]
    public async Task Contribuinte_NaoProvisionado_Recusado()
    {
        var amb = new Ambiente();
        var a = Guid.NewGuid();
        amb.Alvos.Alvos.Add(AlvosBackup.Provisionando(a));

        var acao = async () => await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.Contribuinte, a, PeriodicidadeBackup.Diario);

        await acao.Should().ThrowAsync<BackupInvalidoException>();
        amb.Repo.Registros.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Contribuinte_SemId_Recusado(string? id)
    {
        var amb = new Ambiente();

        var acao = async () => await amb.Criar().ExecutarAsync(
            AlvoBackupSobDemanda.Contribuinte, id is null ? null : Guid.Parse(id), PeriodicidadeBackup.Diario);

        await acao.Should().ThrowAsync<EntradaInvalidaException>();
        amb.Bloqueio.Chaves.Should().BeEmpty();
    }

    [Fact]
    public async Task AlvoOuPeriodicidadeForaDoEnum_Recusado()
    {
        var amb = new Ambiente();

        var alvoInvalido = async () => await amb.Criar().ExecutarAsync((AlvoBackupSobDemanda)99, null, PeriodicidadeBackup.Diario);
        var periodicidadeInvalida = async () => await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.Catalogo, null, (PeriodicidadeBackup)99);

        await alvoInvalido.Should().ThrowAsync<EntradaInvalidaException>();
        await periodicidadeInvalida.Should().ThrowAsync<EntradaInvalidaException>();
        amb.Repo.Registros.Should().BeEmpty();
    }

    [Fact]
    public async Task OutroBackupManualEmAndamento_Recusado()
    {
        var amb = new Ambiente();
        amb.Bloqueio.Ocupado = true;

        var acao = async () => await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.Catalogo, null, PeriodicidadeBackup.Diario);

        await acao.Should().ThrowAsync<OperacaoBackupEmAndamentoException>();
        amb.Repo.Registros.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecutorIndisponivel_Bloqueado_NuncaSimulado()
    {
        var amb = new Ambiente();
        amb.Executor.Disponivel = false;

        var acao = async () => await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.Catalogo, null, PeriodicidadeBackup.Diario);

        await acao.Should().ThrowAsync<ExecutorBackupIndisponivelException>();
        amb.Repo.Registros.Should().BeEmpty();
        amb.Bloqueio.Liberacoes.Should().Be(1);
    }

    [Fact]
    public async Task SemPermissao_Negado_ParaSituacaoEExecucao()
    {
        var amb = new Ambiente();
        amb.Autz.Permite = false;

        var situacao = async () => await amb.Criar().ObterSituacaoAsync();
        var executar = async () => await amb.Criar().ExecutarAsync(AlvoBackupSobDemanda.Catalogo, null, PeriodicidadeBackup.Diario);

        await situacao.Should().ThrowAsync<AutorizacaoNegadaException>();
        await executar.Should().ThrowAsync<AutorizacaoNegadaException>();
        amb.Bloqueio.Chaves.Should().BeEmpty();
    }

    [Fact]
    public async Task Situacao_RefleteConfiguracao_SemExporReferencias()
    {
        var amb = new Ambiente();
        amb.Executor.Disponivel = false;

        var situacao = await amb.Criar().ObterSituacaoAsync();

        situacao.ExecutorDisponivel.Should().BeFalse();
        situacao.CriptografarEmRepouso.Should().BeTrue();
        situacao.ChaveCriptografiaConfigurada.Should().BeTrue();
        situacao.AgendamentoHabilitado.Should().BeTrue();
        situacao.HorarioAgendamentoUtc.Should().Be("03:00");
        situacao.OffsiteConfigurado.Should().BeTrue();
        situacao.RetencaoDiariaDias.Should().Be(7);
        situacao.ToString().Should().NotContain("ref").And.NotContain("offsite"); // só ligado/desligado, nunca o valor
    }
}
