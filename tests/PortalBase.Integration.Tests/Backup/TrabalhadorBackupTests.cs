using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Backup;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Observabilidade;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Observabilidade;
using PortalBase.Infrastructure.Opcoes;
using PortalBase.Infrastructure.Seguranca;
using PortalBase.Integration.Tests.Infra;
using PortalBase.Worker;

namespace PortalBase.Integration.Tests.Backup;

/// <summary>
/// Trabalhador do BACKUP AGENDADO com a autorização de plataforma REAL: o Worker define o principal de sistema, que
/// recebe só <c>plataforma.backup.executar</c> (sem lista, sem perfil, sem MFA); a janela roda uma vez (reinício não
/// duplica); executor fora ⇒ tentativas LIMITADAS e espaçadas, erro visível na saúde; nova janela recomeça. Sem banco:
/// catálogo de backups, store e executor em memória.
/// </summary>
public sealed class TrabalhadorBackupTests
{
    private static readonly TimeOnly Tres = new(3, 0);
    private static readonly DateTimeOffset Dia = new(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);

    private sealed class Ambiente
    {
        public RegistrosMemoria Registros { get; } = new();
        public ExecutorContado Executor { get; } = new();
        public AuditoriaColetada Auditoria { get; } = new();
        public RegistroSaudeWorkers Saude { get; } = new();
        public RelogioAjustavel Relogio { get; } = new();
        public OpcoesBackup Opcoes { get; } = new() { AgendamentoHabilitado = true, CriptografarEmRepouso = false, TentativasPorJanela = 3, IntervaloEntreTentativasMinutos = 15 };
        public List<AlvoBackupTenant> Tenants { get; } = [new(Guid.NewGuid(), "portalbase_tenant_a", EstadoProvisionamento.Ativo)];
        public string NomeAmbiente { get; set; } = Environments.Development;

        public TrabalhadorBackup Criar()
        {
            var servicos = new ServiceCollection();
            servicos.AddScoped<ContextoSeguranca>();
            servicos.AddScoped<IContextoSegurancaMutavel>(sp => sp.GetRequiredService<ContextoSeguranca>());
            servicos.AddScoped<IContextoSeguranca>(sp => sp.GetRequiredService<ContextoSeguranca>());
            servicos.AddScoped<ContextoAuditoria>();
            servicos.AddScoped<IContextoAuditoriaMutavel>(sp => sp.GetRequiredService<ContextoAuditoria>());
            servicos.AddSingleton<IRelogio>(Relogio);
            servicos.AddSingleton<IAuditoria>(Auditoria);
            servicos.AddSingleton<IRepositorioUsuario>(new RepositorioUsuarioPlataformaFake());
            servicos.AddSingleton(new OpcoesAutorizacaoPlataforma { ExigirMfa = true }); // lista vazia: só o principal de sistema
            servicos.AddScoped<IAutorizacaoPlataforma, AutorizacaoPlataforma>();
            servicos.AddSingleton(Opcoes);
            servicos.AddSingleton<IProvedorAlvosBackup>(new AlvosFixos(Tenants));
            servicos.AddSingleton<IExecutorBackupBanco>(Executor);
            servicos.AddSingleton<IArmazenamentoBackup>(new StoreMemoria());
            servicos.AddSingleton<ICriptografiaBackup>(new CriptoIdentidade());
            servicos.AddSingleton<IRepositorioRegistroBackup>(Registros);
            servicos.AddSingleton<IDetectorMigracaoEmAndamento>(new SemMigracao());
            servicos.AddSingleton<IAreaTrabalhoTemporaria>(new AreaMemoria());
            servicos.AddSingleton<ICopiaExternaBackup>(new SemCopiaExterna());
            servicos.AddSingleton<IEmpacotadorArquivosTenant>(new EmpacotadorFixo());
            servicos.AddSingleton<PoliticaRetencaoBackup>();
            servicos.AddScoped<ServicoBackup>();
            servicos.AddScoped<ExecucaoBackupAgendado>();
            var provedor = servicos.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            return new TrabalhadorBackup(
                provedor.GetRequiredService<IServiceScopeFactory>(), Opcoes, Saude, NullLogger<TrabalhadorBackup>.Instance, new AmbienteHost(NomeAmbiente));
        }

        /// <summary>Um passo do agendamento no instante simulado (o relógio do serviço acompanha).</summary>
        public Task PassoAsync(TrabalhadorBackup worker, DateTimeOffset agora)
        {
            Relogio.AgoraUtc = agora;
            return worker.VerificarAsync(Tres, agora, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Janela_Roda_Uma_Vez_Com_O_Principal_De_Sistema_E_Reinicio_Nao_Duplica()
    {
        var amb = new Ambiente();
        var worker = amb.Criar();

        await amb.PassoAsync(worker, Dia.AddMinutes(1));

        amb.Registros.Itens.Should().HaveCount(3, "catálogo + base e arquivos do contribuinte provisionado");
        amb.Registros.Itens.Select(r => r.Tipo).Should().BeEquivalentTo([TipoBackup.CatalogoCompleto, TipoBackup.TenantCompleto, TipoBackup.Storage]);
        amb.Registros.Itens.Should().OnlyContain(r => r.Status == StatusBackup.Concluido);
        amb.Auditoria.Eventos.Where(e => e.Operacao == AcaoAuditoria.BackupCompleted)
            .Should().HaveCount(3).And.OnlyContain(e => e.PrincipalId == ExecucaoBackupAgendado.PrincipalSistema);
        amb.Saude.Obter(TrabalhadorBackup.NomeWorker)!.TotalProcessado.Should().Be(3);

        await amb.PassoAsync(worker, Dia.AddHours(1));
        await amb.PassoAsync(amb.Criar(), Dia.AddHours(2)); // "reinício" do Worker
        amb.Registros.Itens.Should().HaveCount(3, "a janela já atendida não é refeita");

        await amb.PassoAsync(worker, Dia.AddDays(1).AddMinutes(1));
        amb.Registros.Itens.Should().HaveCount(6, "a janela do dia seguinte é nova");
    }

    [Fact]
    public async Task Executor_Fora_Tenta_No_Maximo_Tres_Vezes_Espacadas_E_Mostra_O_Erro()
    {
        var amb = new Ambiente();
        amb.Executor.Disponivel = false;
        var worker = amb.Criar();

        foreach (var minuto in new[] { 0, 1, 5, 15, 20, 30, 45, 60, 120 })
        {
            await amb.PassoAsync(worker, Dia.AddMinutes(minuto));
        }

        amb.Executor.Consultas.Should().Be(3, "tentativas nos minutos 0, 15 e 30; depois o limite da janela");
        amb.Registros.Itens.Should().BeEmpty("nada é registrado como backup sem execução real");
        var saude = amb.Saude.Obter(TrabalhadorBackup.NomeWorker)!;
        saude.UltimoErroCodigo.Should().Be("BackupBloqueado");
        saude.TotalErros.Should().Be(3);

        amb.Executor.Disponivel = true;
        await amb.PassoAsync(worker, Dia.AddDays(1));
        amb.Registros.Itens.Should().HaveCount(3, "na janela seguinte volta a tentar");
    }

    [Fact]
    public async Task Worker_Parado_No_Horario_Recupera_A_Janela_Perdida_Ao_Subir()
    {
        var amb = new Ambiente();

        // 02:00 do dia 8: a janela vigente é a das 03:00 do dia 7, que não foi feita.
        await amb.PassoAsync(amb.Criar(), Dia.AddHours(23));

        amb.Registros.Itens.Should().HaveCount(3);
        amb.Registros.Itens.Should().OnlyContain(r => r.IniciadoEmUtc == Dia.AddHours(23));
    }

    [Fact]
    public async Task Processo_Que_Caiu_No_Meio_Nao_Deixa_A_Janela_Como_Atendida()
    {
        // JOB-001: o Worker caiu durante o backup das 03:00 e o registro ficou "Iniciado". Antes, esse registro contava
        // como "janela atendida" e o dia ficava sem backup. Agora, passado o limite, ele vira falho e a janela é refeita.
        var amb = new Ambiente();
        var interrompido = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "portalbase_catalogo", Dia.AddMinutes(1));
        await amb.Registros.AdicionarAsync(interrompido);

        await amb.PassoAsync(amb.Criar(), Dia.AddMinutes(2));
        amb.Registros.Itens.Should().ContainSingle("ainda dentro do limite: pode ser outra instância trabalhando");

        await amb.PassoAsync(amb.Criar(), Dia.Add(ServicoBackup.LimiteEmAndamento).AddMinutes(2));

        interrompido.Status.Should().Be(StatusBackup.Falhou);
        interrompido.UltimoErro.Should().Be("Interrompido");
        amb.Registros.Itens.Where(r => r.Status == StatusBackup.Concluido).Should().HaveCount(3, "a janela foi refeita");
        amb.Auditoria.Eventos.Should().Contain(e => e.Operacao == AcaoAuditoria.BackupFailed && e.RecursoId == interrompido.Id.ToString());
    }

    [Fact]
    public async Task Producao_Com_Backup_Desligado_Fica_Visivel_Como_Erro()
    {
        // BKP-002: em produção, "sem backup automático" não é silencioso — aparece como erro em Operações.
        var amb = new Ambiente { NomeAmbiente = Environments.Production };
        amb.Opcoes.AgendamentoHabilitado = false;

        var worker = amb.Criar();
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!; // desligado: ExecuteAsync termina logo depois de registrar

        var saude = amb.Saude.Obter(TrabalhadorBackup.NomeWorker);
        saude.Should().NotBeNull();
        saude!.UltimoErroCodigo.Should().Be("BackupAgendadoDesligado");

        var dev = new Ambiente();
        dev.Opcoes.AgendamentoHabilitado = false;
        var workerDev = dev.Criar();
        await workerDev.StartAsync(CancellationToken.None);
        await workerDev.ExecuteTask!;
        dev.Saude.Obter(TrabalhadorBackup.NomeWorker).Should().BeNull("fora de produção, desligado é uma escolha");
    }

    // ---- Dobras em memória ----

    private sealed class RegistrosMemoria : IRepositorioRegistroBackup
    {
        private readonly ConcurrentDictionary<Guid, RegistroBackup> _itens = new();

        public IReadOnlyCollection<RegistroBackup> Itens => _itens.Values.ToList();

        public Task AdicionarAsync(RegistroBackup registro, CancellationToken cancellationToken = default)
        {
            _itens[registro.Id] = registro;
            return Task.CompletedTask;
        }

        public Task AtualizarAsync(RegistroBackup registro, CancellationToken cancellationToken = default)
        {
            _itens[registro.Id] = registro;
            return Task.CompletedTask;
        }

        public Task<RegistroBackup?> ObterAsync(Guid backupId, CancellationToken cancellationToken = default)
            => Task.FromResult(_itens.GetValueOrDefault(backupId));

        public Task<IReadOnlyList<RegistroBackup>> ListarPorTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegistroBackup>>(_itens.Values.Where(r => r.TenantId == tenantId).ToList());

        public Task<IReadOnlyList<RegistroBackup>> ListarConcluidosAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegistroBackup>>(_itens.Values.Where(r => r.Status == StatusBackup.Concluido).ToList());

        public Task<int> ContarConcluidosAsync(Guid? tenantId, TipoBackup tipo, CancellationToken cancellationToken = default)
            => Task.FromResult(_itens.Values.Count(r => r.TenantId == tenantId && r.Tipo == tipo && r.Status == StatusBackup.Concluido));

        public Task<bool> ExisteConcluidoOuEmAndamentoDesdeAsync(
            TipoBackup tipo, DateTimeOffset desdeUtc, DateTimeOffset emAndamentoDesdeUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(_itens.Values.Any(r => r.Tipo == tipo && r.IniciadoEmUtc >= desdeUtc
                && (r.Status == StatusBackup.Concluido || (r.Status == StatusBackup.Iniciado && r.IniciadoEmUtc >= emAndamentoDesdeUtc))));

        public Task<IReadOnlyList<RegistroBackup>> ListarIniciadosAntesDeAsync(DateTimeOffset antesDeUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegistroBackup>>(
                _itens.Values.Where(r => r.Status == StatusBackup.Iniciado && r.IniciadoEmUtc < antesDeUtc).ToList());

        public Task<IReadOnlyList<RegistroBackup>> ListarConcluidosVencidosAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegistroBackup>>(
                _itens.Values.Where(r => r.Status == StatusBackup.Concluido && r.RetencaoAteUtc is { } ate && ate <= agoraUtc).ToList());

        public Task<IReadOnlyList<RegistroBackup>> ListarRecentesAsync(int quantidade, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegistroBackup>>(_itens.Values.OrderByDescending(r => r.IniciadoEmUtc).Take(quantidade).ToList());
    }

    private sealed class ExecutorContado : IExecutorBackupBanco
    {
        private int _consultas;
        private bool _disponivel = true;

        public int Consultas => _consultas;

        public bool Disponivel
        {
            get
            {
                Interlocked.Increment(ref _consultas);
                return _disponivel;
            }

            set => _disponivel = value;
        }

        public Task GerarDumpAsync(string nomeBaseLogica, Stream destino, CancellationToken cancellationToken = default)
            => destino.WriteAsync("DUMP"u8.ToArray(), cancellationToken).AsTask();

        public Task RestaurarEmBaseTemporariaAsync(string nomeBaseTemporaria, Stream origem, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class AuditoriaColetada : IAuditoria
    {
        public ConcurrentQueue<EventoAuditoria> Eventos { get; } = new();

        public ValueTask RegistrarAsync(EventoAuditoria evento, CancellationToken cancellationToken = default)
        {
            Eventos.Enqueue(evento);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AlvosFixos(List<AlvoBackupTenant> tenants) : IProvedorAlvosBackup
    {
        public string? NomeBaseCatalogo => "portalbase_catalogo";

        public Task<IReadOnlyList<AlvoBackupTenant>> ListarBackupReadyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlvoBackupTenant>>(tenants);

        public Task<AlvoBackupTenant?> ObterAsync(Guid tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(tenants.FirstOrDefault(t => t.TenantId == tenantId));
    }

    private sealed class StoreMemoria : IArmazenamentoBackup
    {
        private readonly ConcurrentDictionary<string, byte[]> _artefatos = new(StringComparer.Ordinal);

        public async Task<string> GravarAsync(string referenciaLogica, Stream conteudo, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await conteudo.CopyToAsync(ms, cancellationToken);
            _artefatos[referenciaLogica] = ms.ToArray();
            return referenciaLogica;
        }

        public Task<Stream> AbrirAsync(string referenciaLogica, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(_artefatos[referenciaLogica]));

        public Task<bool> ExisteAsync(string referenciaLogica, CancellationToken cancellationToken = default)
            => Task.FromResult(_artefatos.ContainsKey(referenciaLogica));

        public Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default)
        {
            _artefatos.TryRemove(referenciaLogica, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class CriptoIdentidade : ICriptografiaBackup
    {
        public string ChaveRef => "ref";

        public Task CriptografarAsync(Stream claro, Stream cifradoDestino, CancellationToken cancellationToken = default)
            => claro.CopyToAsync(cifradoDestino, cancellationToken);

        public Task DecriptografarAsync(Stream cifrado, Stream claroDestino, CancellationToken cancellationToken = default)
            => cifrado.CopyToAsync(claroDestino, cancellationToken);
    }

    private sealed class SemMigracao : IDetectorMigracaoEmAndamento
    {
        public Task<bool> MigracaoEmAndamentoAsync(Guid? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RelogioAjustavel : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; set; } = Dia;
    }

    private sealed class AreaMemoria : IAreaTrabalhoTemporaria
    {
        public Task<Stream> CriarAsync(CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream());
    }

    private sealed class SemCopiaExterna : ICopiaExternaBackup
    {
        public bool Configurada => false;

        public Task CopiarAsync(string referenciaLogica, Stream artefato, string hashSha256Esperado, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Cópia externa não configurada.");

        public Task RemoverAsync(string referenciaLogica, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmpacotadorFixo : IEmpacotadorArquivosTenant
    {
        public Task EmpacotarAsync(Guid tenantId, Stream destino, CancellationToken cancellationToken = default)
            => destino.WriteAsync("ZIP"u8.ToArray(), cancellationToken).AsTask();
    }

    private sealed class AmbienteHost(string nome) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nome;

        public string ApplicationName { get; set; } = "PortalBase.Worker";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
