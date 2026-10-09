using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Backup;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Provisionamento;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Tests.Backup;

/// <summary>
/// Restauração pela TELA da plataforma: nunca sobrescreve sem volta (base de preparo → troca atômica → base preservada),
/// só o backup DO MESMO contribuinte e DA MESMA base (cross-tenant negado e auditado), contribuinte em manutenção durante
/// a troca, falha na troca ⇒ volta ao estado anterior, falha nas migrations ⇒ fica desabilitado (fail-closed).
/// O teste de restauração usa base temporária e sempre a remove.
/// </summary>
public sealed class RestauracaoPlataformaTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string Justificativa = "Erro na importação de setembro: voltar ao backup de ontem.";

    /// <summary>Bases por nome (existência e marca de proprietário) — o que o MySQL teria.</summary>
    private sealed class BancosPorNomeFake : IAdministradorBancoTenant
    {
        public HashSet<string> Bases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ProprietarioBaseTenant> Donos { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Removidas { get; } = [];

        public ValueTask<bool> BancoExisteAsync(string nomeBase, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Bases.Contains(nomeBase));

        public ValueTask CriarBancoSeAusenteAsync(string nomeBase, CancellationToken cancellationToken = default)
        {
            Bases.Add(nomeBase);
            return ValueTask.CompletedTask;
        }

        public ValueTask ConcederAcessoRuntimeAsync(string nomeBase, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoverBancoAsync(string nomeBase, CancellationToken cancellationToken = default)
        {
            Bases.Remove(nomeBase);
            Donos.Remove(nomeBase);
            Removidas.Add(nomeBase);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ProprietarioBaseTenant> ObterProprietarioAsync(string nomeBase, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Donos.GetValueOrDefault(nomeBase) ?? ProprietarioBaseTenant.BaseVazia);

        public ValueTask RegistrarProprietarioAsync(string nomeBase, Guid tenantId, string cnpj, CancellationToken cancellationToken = default)
        {
            Donos[nomeBase] = new ProprietarioBaseTenant(false, tenantId, cnpj);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Executor que "cria" a base restaurada com a marca de proprietário configurada (a do dump).</summary>
    private sealed class ExecutorComBasesFake(BancosPorNomeFake bancos) : IExecutorBackupBanco
    {
        public bool Disponivel { get; set; } = true;

        /// <summary>Dono gravado no dump (null ⇒ base restaurada vazia).</summary>
        public ProprietarioBaseTenant? DonoNoDump { get; set; }

        public List<string> Restauradas { get; } = [];

        public async Task GerarDumpAsync(string nomeBaseLogica, Stream destino, CancellationToken cancellationToken = default)
            => await destino.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"DUMP::{nomeBaseLogica}"), cancellationToken);

        public Task RestaurarEmBaseTemporariaAsync(string nomeBaseTemporaria, Stream origem, CancellationToken cancellationToken = default)
        {
            Restauradas.Add(nomeBaseTemporaria);
            bancos.Bases.Add(nomeBaseTemporaria);
            if (DonoNoDump is not null)
            {
                bancos.Donos[nomeBaseTemporaria] = DonoNoDump;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Troca e bloqueio em memória: registra os pares e simula a base de preparo esvaziada/removida.</summary>
    private sealed class OperacoesFake(BancosPorNomeFake bancos) : IOperacoesBaseRestauracao
    {
        public bool Ocupado { get; set; }

        public bool FalharTroca { get; set; }

        public List<string> Bloqueios { get; } = [];

        public int Liberacoes { get; private set; }

        public List<(string Producao, string Restaurada, string Preservada)> Trocas { get; } = [];

        public Action? AoTrocar { get; set; }

        public Task<IAsyncDisposable> BloquearAsync(string chave, CancellationToken cancellationToken = default)
        {
            if (Ocupado)
            {
                throw new OperacaoBackupEmAndamentoException();
            }

            Bloqueios.Add(chave);
            return Task.FromResult<IAsyncDisposable>(new Liberacao(() => Liberacoes++));
        }

        public Task TrocarTabelasAsync(string baseProducao, string baseRestaurada, string basePreservada, CancellationToken cancellationToken = default)
        {
            AoTrocar?.Invoke();
            if (FalharTroca)
            {
                throw new InvalidOperationException("Falha simulada no RENAME TABLE.");
            }

            Trocas.Add((baseProducao, baseRestaurada, basePreservada));
            bancos.Bases.Add(basePreservada);
            bancos.Bases.Remove(baseRestaurada);
            if (bancos.Donos.Remove(baseRestaurada, out var dono))
            {
                bancos.Donos[baseProducao] = dono;
            }

            return Task.CompletedTask;
        }

        private sealed class Liberacao(Action aoLiberar) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                aoLiberar();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Ambiente
    {
        public Ambiente()
        {
            Executor = new ExecutorComBasesFake(Bancos);
            Operacoes = new OperacoesFake(Bancos);
        }

        public BancosPorNomeFake Bancos { get; } = new();
        public ExecutorComBasesFake Executor { get; }
        public OperacoesFake Operacoes { get; }
        public ProvedorAlvosBackupFake Alvos { get; } = new();
        public ArmazenamentoBackupMemoria Store { get; } = new();
        public CriptografiaBackupIdentidade Cripto { get; } = new();
        public RepositorioRegistroBackupMemoria Repo { get; } = new();
        public AutorizacaoPlataformaFake Autz { get; } = new();
        public AuditoriaFake Auditoria { get; } = new();
        public ContextoSegurancaFake Seguranca { get; } = new();
        public MigradorFake Migrador { get; } = new();
        public RepositorioTenantFake Tenants { get; } = new();
        public UnitOfWorkFake UnitOfWork { get; } = new();
        public OpcoesBackup Opcoes { get; } = new() { CriptografarEmRepouso = true, ChaveCriptografiaRef = "ref" };

        public ServicoBackup ServicoBackup() => new(
            Alvos, Executor, Store, Cripto, Repo, new DetectorMigracaoFake(),
            new PoliticaRetencaoBackup(Opcoes), Autz, Auditoria, Seguranca, new RelogioFixo(Agora), Opcoes,
            new AreaTemporariaMemoria(), new CopiaExternaMemoria { Configurada = false }, new EmpacotadorArquivosFake());

        public RestauracaoPlataforma Restauracao() => new(
            new ServicoRestauracao(Repo, Store, Cripto, Executor, Alvos, Autz, Auditoria, Seguranca, new RelogioFixo(Agora)),
            Repo, Alvos, Bancos, Operacoes, Migrador, Tenants, UnitOfWork, Autz, Auditoria, Seguranca, new RelogioFixo(Agora));

        /// <summary>Contribuinte no estado pedido, com base de produção e um backup concluído dela.</summary>
        public async Task<(Tenant Tenant, string Producao, Guid BackupId)> ContribuinteComBackupAsync(
            EstadoProvisionamento estado = EstadoProvisionamento.Ativo, string? producao = null)
        {
            var tenant = Tenant.Criar(Cnpj.Criar("11444777000161"), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]),
                "Empresa", "cofre://x", Agora);
            tenant.IniciarProvisionamento(Agora);
            tenant.ConcluirProvisionamento("v1", "v1", Agora);
            if (estado is EstadoProvisionamento.Ativo or EstadoProvisionamento.Desabilitado)
            {
                tenant.Ativar(Agora);
            }

            if (estado == EstadoProvisionamento.Desabilitado)
            {
                tenant.Desabilitar(Agora);
            }

            Tenants.Adicionados.Add(tenant);
            var nomeBase = producao ?? $"portalbase_tenant_{tenant.Id:N}"[..40];
            // O backup foi tirado quando o contribuinte estava ativo; depois ele passa ao estado pedido.
            var alvo = new AlvoBackupTenant(tenant.Id, nomeBase, EstadoProvisionamento.Ativo);
            Alvos.Alvos.Add(alvo);
            Bancos.Bases.Add(nomeBase);
            Bancos.Donos[nomeBase] = new ProprietarioBaseTenant(false, tenant.Id, "11444777000161");
            Executor.DonoNoDump = new ProprietarioBaseTenant(false, tenant.Id, "11444777000161");

            var resultado = await ServicoBackup().ExecutarBackupTenantAsync(tenant.Id, PeriodicidadeBackup.Diario);
            Alvos.Alvos[Alvos.Alvos.IndexOf(alvo)] = alvo with { Estado = estado };
            return (tenant, nomeBase, resultado.BackupId);
        }

        public bool TemEvento(string acao) => Auditoria.Eventos.Exists(e => e.Operacao == acao);
    }

    [Fact]
    public async Task Restaurar_ContribuinteAtivo_PreparaTrocaMigraReativaEAudita()
    {
        var amb = new Ambiente();
        var (tenant, producao, backupId) = await amb.ContribuinteComBackupAsync();
        EstadoProvisionamento? estadoDuranteTroca = null;
        amb.Operacoes.AoTrocar = () => estadoDuranteTroca = tenant.Estado;

        var resultado = await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        resultado.ContribuinteReativado.Should().BeTrue();
        resultado.SchemaVersion.Should().NotBeNullOrWhiteSpace();
        estadoDuranteTroca.Should().Be(EstadoProvisionamento.Desabilitado); // ninguém grava durante a troca
        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);

        var troca = amb.Operacoes.Trocas.Should().ContainSingle().Subject;
        troca.Producao.Should().Be(producao);
        troca.Restaurada.Should().Be($"{producao}_rst20261007120000");
        troca.Preservada.Should().Be($"{producao}_ant20261007120000");
        amb.Executor.Restauradas.Should().Equal(troca.Restaurada); // restaurou no PREPARO, nunca na produção
        amb.Bancos.Bases.Should().Contain([producao, troca.Preservada]).And.NotContain(troca.Restaurada);
        amb.Migrador.Migracoes.Should().Be(1);

        amb.Operacoes.Bloqueios.Should().Equal($"portalbase_rest_{tenant.Id:N}");
        amb.Operacoes.Liberacoes.Should().Be(1);
        var evento = amb.Auditoria.Eventos.Single(e => e.Operacao == AcaoAuditoria.RestoreAppliedToTenant);
        evento.Resultado.Should().Be(ResultadoAuditoria.Permitido);
        evento.Tenant.Should().Be(tenant.Id.ToString());
        evento.Motivo.Should().Be(Justificativa);
        evento.Metadados.Should().ContainKey("basePreservada").WhoseValue.Should().Be(troca.Preservada);
    }

    [Fact]
    public async Task Restaurar_ContribuinteDesabilitado_ContinuaDesabilitado()
    {
        var amb = new Ambiente();
        var (tenant, _, backupId) = await amb.ContribuinteComBackupAsync(EstadoProvisionamento.Desabilitado);

        var resultado = await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        resultado.ContribuinteReativado.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoProvisionamento.Desabilitado);
        amb.Operacoes.Trocas.Should().ContainSingle();
    }

    [Fact]
    public async Task Restaurar_BackupDeOutroContribuinte_NegadoEAuditado_SemTocarEmNada()
    {
        var amb = new Ambiente();
        var (_, _, backupDeA) = await amb.ContribuinteComBackupAsync();
        var (b, producaoB, _) = await amb.ContribuinteComBackupAsync();
        amb.Executor.Restauradas.Clear();

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupDeA, b.Id, Justificativa);

        await acao.Should().ThrowAsync<RestauracaoCrossTenantException>();
        amb.TemEvento(AcaoAuditoria.RestoreCrossTenantDenied).Should().BeTrue();
        amb.Executor.Restauradas.Should().BeEmpty();
        amb.Operacoes.Trocas.Should().BeEmpty();
        amb.Operacoes.Bloqueios.Should().BeEmpty();
        b.Estado.Should().Be(EstadoProvisionamento.Ativo);
        amb.Bancos.Bases.Should().Contain(producaoB);
    }

    [Fact]
    public async Task Restaurar_BackupDeOutraBaseFisica_Negado()
    {
        var amb = new Ambiente();
        var (tenant, _, backupId) = await amb.ContribuinteComBackupAsync();
        // O contribuinte passou a apontar para outra base física depois do backup: o backup antigo não volta para ela.
        var alvo = amb.Alvos.Alvos.Single();
        amb.Alvos.Alvos[0] = alvo with { NomeBaseFisica = "portalbase_tenant_outra" };

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<RestauracaoCrossTenantException>();
        amb.TemEvento(AcaoAuditoria.RestoreCrossTenantDenied).Should().BeTrue();
        amb.Operacoes.Trocas.Should().BeEmpty();
    }

    [Fact]
    public async Task Restaurar_MarcaDeProprietarioDeOutroContribuinte_Negado_PreparoRemovido()
    {
        var amb = new Ambiente();
        var (tenant, producao, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Executor.DonoNoDump = new ProprietarioBaseTenant(false, Guid.NewGuid(), "11222333000181");

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<RestauracaoCrossTenantException>();
        amb.TemEvento(AcaoAuditoria.RestoreCrossTenantDenied).Should().BeTrue();
        amb.Bancos.Removidas.Should().ContainSingle().Which.Should().StartWith($"{producao}_rst");
        amb.Bancos.Bases.Should().ContainSingle().Which.Should().Be(producao);
        amb.Operacoes.Trocas.Should().BeEmpty();
        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
    }

    [Fact]
    public async Task Restaurar_BackupVazio_Recusado_PreparoRemovido()
    {
        var amb = new Ambiente();
        var (tenant, producao, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Executor.DonoNoDump = null;

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<BackupInvalidoException>();
        amb.Bancos.Bases.Should().ContainSingle().Which.Should().Be(producao);
        amb.Operacoes.Trocas.Should().BeEmpty();
    }

    [Fact]
    public async Task Restaurar_FalhaNaTroca_VoltaAoEstadoAnterior_EAudita()
    {
        var amb = new Ambiente();
        var (tenant, producao, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Operacoes.FalharTroca = true;

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<InvalidOperationException>();
        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo); // reativado: a produção não mudou
        amb.Bancos.Bases.Should().ContainSingle().Which.Should().Be(producao); // preparo removido
        amb.Migrador.Migracoes.Should().Be(0);
        var falha = amb.Auditoria.Eventos.Single(e => e.Operacao == AcaoAuditoria.RestoreApplyFailed);
        falha.Metadados.Should().ContainKey("etapa").WhoseValue.Should().Be("troca");
        amb.TemEvento(AcaoAuditoria.RestoreAppliedToTenant).Should().BeFalse();
        amb.Operacoes.Liberacoes.Should().Be(1);
    }

    [Fact]
    public async Task Restaurar_FalhaNasMigrations_ContribuinteFicaDesabilitado()
    {
        var amb = new Ambiente();
        var (tenant, _, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Migrador.FalharMigracao = true;

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<MigracaoAposRestauracaoException>();
        tenant.Estado.Should().Be(EstadoProvisionamento.Desabilitado); // fail-closed
        tenant.UltimoErro.Should().Contain("migrations falharam");
        var falha = amb.Auditoria.Eventos.Single(e => e.Operacao == AcaoAuditoria.RestoreApplyFailed);
        falha.Metadados.Should().ContainKey("etapa").WhoseValue.Should().Be("migrations");
        falha.Metadados.Should().ContainKey("basePreservada");
        amb.TemEvento(AcaoAuditoria.RestoreAppliedToTenant).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   curto  ")]
    public async Task Restaurar_SemJustificativa_Recusado_AntesDeQualquerAcao(string? justificativa)
    {
        var amb = new Ambiente();
        var (tenant, _, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Executor.Restauradas.Clear();

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, justificativa);

        await acao.Should().ThrowAsync<EntradaInvalidaException>();
        amb.Executor.Restauradas.Should().BeEmpty();
        amb.Operacoes.Bloqueios.Should().BeEmpty();
    }

    [Fact]
    public async Task Restaurar_JustificativaLonga_TruncadaNaAuditoria()
    {
        var amb = new Ambiente();
        var (tenant, _, backupId) = await amb.ContribuinteComBackupAsync();

        await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, new string('x', 1000));

        amb.Auditoria.Eventos.Single(e => e.Operacao == AcaoAuditoria.RestoreAppliedToTenant)
            .Motivo.Should().HaveLength(RestauracaoPlataforma.JustificativaMaxima);
    }

    [Fact]
    public async Task Restaurar_SemPermissaoDePlataforma_Negado()
    {
        var amb = new Ambiente();
        var (tenant, _, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Autz.Permite = false;

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        amb.Operacoes.Trocas.Should().BeEmpty();
    }

    [Fact]
    public async Task Restaurar_OutraRestauracaoEmAndamento_Recusado()
    {
        var amb = new Ambiente();
        var (tenant, _, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Executor.Restauradas.Clear();
        amb.Operacoes.Ocupado = true;

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<OperacaoBackupEmAndamentoException>();
        amb.Executor.Restauradas.Should().BeEmpty();
        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
    }

    [Fact]
    public async Task Restaurar_BaseDePreparoJaExiste_Recusado_SemApagarNada()
    {
        var amb = new Ambiente();
        var (tenant, producao, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Bancos.Bases.Add($"{producao}_rst20261007120000");

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<OperacaoBackupEmAndamentoException>();
        amb.Bancos.Removidas.Should().BeEmpty();
        amb.Operacoes.Liberacoes.Should().Be(1);
    }

    [Fact]
    public async Task Restaurar_BackupIncompleto_Recusado()
    {
        var amb = new Ambiente();
        var (tenant, producao, _) = await amb.ContribuinteComBackupAsync();
        var incompleto = RegistroBackup.Iniciar(TipoBackup.TenantCompleto, tenant.Id, producao, Agora);
        await amb.Repo.AdicionarAsync(incompleto);

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(incompleto.Id, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<BackupInvalidoException>();
        amb.Operacoes.Trocas.Should().BeEmpty();
    }

    [Fact]
    public async Task Restaurar_ContribuinteNaoProvisionado_Recusado()
    {
        var amb = new Ambiente();
        var (tenant, _, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Alvos.Alvos[0] = amb.Alvos.Alvos[0] with { Estado = EstadoProvisionamento.Falhado };

        var acao = async () => await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        await acao.Should().ThrowAsync<BackupInvalidoException>();
        amb.Operacoes.Bloqueios.Should().BeEmpty();
    }

    [Fact]
    public async Task Restaurar_NomeDeBaseLongo_NomesDerivadosCabemNoLimiteDoMySql()
    {
        var amb = new Ambiente();
        var longa = "portalbase_tenant_" + new string('a', 46); // 64 caracteres
        var (tenant, _, backupId) = await amb.ContribuinteComBackupAsync(producao: longa);

        await amb.Restauracao().RestaurarContribuinteAsync(backupId, tenant.Id, Justificativa);

        var troca = amb.Operacoes.Trocas.Single();
        troca.Restaurada.Should().HaveLength(64).And.EndWith("_rst20261007120000");
        troca.Preservada.Should().HaveLength(64).And.EndWith("_ant20261007120000");
        troca.Restaurada.Should().NotBe(troca.Preservada);
    }

    [Fact]
    public async Task Testar_BackupDeContribuinte_RestauraEmTemporaria_RemoveEMarcaTestado()
    {
        var amb = new Ambiente();
        var (tenant, producao, backupId) = await amb.ContribuinteComBackupAsync();

        var resultado = await amb.Restauracao().TestarAsync(backupId);

        resultado.Restaurado.Should().BeTrue();
        resultado.BaseTemporariaRemovida.Should().BeTrue();
        var temporaria = amb.Executor.Restauradas.Should().ContainSingle().Subject;
        temporaria.Should().MatchRegex($"^{producao}_tst20261007120000[0-9a-f]{{4}}$"); // derivada da base (grants por prefixo)
        amb.Bancos.Removidas.Should().Equal(temporaria);
        amb.Bancos.Bases.Should().ContainSingle().Which.Should().Be(producao); // produção intocada
        amb.Repo.Registros[backupId].RestoreTestado.Should().BeTrue();
        amb.Operacoes.Trocas.Should().BeEmpty();
        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
    }

    [Fact]
    public async Task Testar_BackupDoCatalogo_RestauraEmTemporariaERemove()
    {
        var amb = new Ambiente();
        var backup = await amb.ServicoBackup().ExecutarBackupCatalogoAsync(PeriodicidadeBackup.Diario, "portalbase_catalogo");

        var resultado = await amb.Restauracao().TestarAsync(backup.BackupId);

        resultado.BaseTemporariaRemovida.Should().BeTrue();
        amb.Bancos.Bases.Should().BeEmpty();
        amb.Bancos.Removidas.Should().ContainSingle().Which.Should().StartWith("portalbase_catalogo_tst");
        amb.Repo.Registros[backup.BackupId].RestoreTestado.Should().BeTrue();
    }

    [Fact]
    public async Task Testar_DonoDivergente_Negado_TemporariaRemovidaMesmoAssim()
    {
        var amb = new Ambiente();
        var (_, producao, backupId) = await amb.ContribuinteComBackupAsync();
        amb.Executor.DonoNoDump = new ProprietarioBaseTenant(false, Guid.NewGuid(), "11222333000181");

        var acao = async () => await amb.Restauracao().TestarAsync(backupId);

        await acao.Should().ThrowAsync<RestauracaoCrossTenantException>();
        amb.Bancos.Bases.Should().ContainSingle().Which.Should().Be(producao);
        amb.Bancos.Removidas.Should().ContainSingle().Which.Should().StartWith($"{producao}_tst");
    }

    [Fact]
    public async Task Testar_BackupInexistente_Recusado()
    {
        var amb = new Ambiente();

        var acao = async () => await amb.Restauracao().TestarAsync(Guid.NewGuid());

        await acao.Should().ThrowAsync<BackupInvalidoException>();
        amb.Executor.Restauradas.Should().BeEmpty();
    }
}
