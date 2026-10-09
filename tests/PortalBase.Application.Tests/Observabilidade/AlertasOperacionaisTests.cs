using FluentAssertions;
using Microsoft.Extensions.Logging;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Backup;
using PortalBase.Application.Notificacoes;
using PortalBase.Application.Observabilidade;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Application.Tests.Observabilidade;

/// <summary>
/// OBS-004 (auditoria de 08/10/2026) — antes, os alertas existiam só no documento: Worker parado, backup que não rodou,
/// disco cheio e e-mails parados ficavam em silêncio. Aqui: as regras de cada alerta e a prova de que eles DISPARAM pelos
/// três caminhos (log para o Visualizador de Eventos, auditoria/incidente e e-mail), sem repetir a cada volta.
/// </summary>
public sealed class AlertasOperacionaisTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly OpcoesObservabilidade Opcoes = new() { LimiteEstagnacaoSegundos = 300 };
    private static readonly Guid TenantA = Guid.Parse("3f2a1b4c-0000-4000-8000-000000000001");

    // ------------------------------------------------------------------ regras (função pura)

    [Fact]
    public void Worker_Fora_Do_Ar_E_Critico_E_Nao_Detalha_Trabalhadores()
    {
        var situacao = new SituacaoServicoWorker(EstadoServicoWorker.Parado, "O serviço do Worker está parado (encerrou há 9 min).", Agora, [], ["outbox"]);

        var alertas = AvaliacaoAlertas.Avaliar(new EntradasAlertas { Worker = situacao }, Agora, Opcoes);

        alertas.Should().ContainSingle();
        alertas[0].Alerta.Codigo.Should().Be("WORKER-FORA-DO-AR");
        alertas[0].Alerta.Severidade.Should().Be(SeveridadeAlerta.Critico);
        alertas[0].Alerta.Descricao.Should().Contain("parado").And.Contain("services.msc");
    }

    [Fact]
    public void Worker_Degradado_Alerta_Cada_Trabalhador_Em_Falha_Ou_Travado()
    {
        var situacao = new SituacaoServicoWorker(EstadoServicoWorker.Degradado, "-", Agora, ["outbox"], ["backup-agendado"]);

        var alertas = AvaliacaoAlertas.Avaliar(new EntradasAlertas { Worker = situacao }, Agora, Opcoes);

        alertas.Select(a => a.Alerta.Codigo).Should().BeEquivalentTo(["WK-FALHA-backup-agendado", "WK-TRAVADO-outbox"]);
        alertas.Should().OnlyContain(a => a.Alerta.Severidade == SeveridadeAlerta.Alto);
    }

    [Fact]
    public void Worker_Em_Execucao_Ou_Nao_Esperado_Nao_Alerta()
    {
        AvaliacaoAlertas.Avaliar(new EntradasAlertas { Worker = Situacao(EstadoServicoWorker.EmExecucao) }, Agora, Opcoes).Should().BeEmpty();
        AvaliacaoAlertas.Avaliar(new EntradasAlertas { Worker = Situacao(EstadoServicoWorker.NaoEsperado) }, Agora, Opcoes).Should().BeEmpty();
    }

    [Fact]
    public void Backup_Atrasado_E_Critico_E_Backup_Desligado_Nao_E_Avaliado()
    {
        var atrasado = AvaliacaoAlertas.Avaliar(new EntradasAlertas { BackupCatalogoRecente = false }, Agora, Opcoes);
        atrasado.Should().ContainSingle(a => a.Alerta.Codigo == "BACKUP-ATRASADO" && a.Alerta.Severidade == SeveridadeAlerta.Critico);
        atrasado[0].Alerta.Descricao.Should().Contain("26 horas");

        AvaliacaoAlertas.Avaliar(new EntradasAlertas { BackupCatalogoRecente = true }, Agora, Opcoes).Should().BeEmpty();
        new EntradasAlertas().FontesAvaliadas.Should().NotContain(FonteAlerta.Backup, "agendamento desligado: nada a concluir sobre backup");
    }

    [Theory]
    [InlineData(500, 1000, null)] // 50% e 500 GB livres
    [InlineData(80, 1000, SeveridadeAlerta.Alto)] // 8% livre
    [InlineData(40, 1000, SeveridadeAlerta.Critico)] // 4% livre (menos que a metade do mínimo)
    [InlineData(4, 26, SeveridadeAlerta.Alto)] // 15% livre, mas só 4 GB
    [InlineData(2, 13, SeveridadeAlerta.Critico)] // 15% livre, só 2 GB (menos que a metade de 5 GB)
    public void Disco_Alerta_Por_Percentual_Ou_Por_GB_Livre(long livreGb, long totalGb, SeveridadeAlerta? esperado)
    {
        const long Gb = 1024L * 1024 * 1024;
        var disco = new EspacoDisco(@"D:\", "backups, logs", livreGb * Gb, totalGb * Gb);

        var alertas = AvaliacaoAlertas.Avaliar(new EntradasAlertas { Discos = [disco] }, Agora, Opcoes);

        if (esperado is null)
        {
            alertas.Should().BeEmpty();
            return;
        }

        alertas.Should().ContainSingle();
        alertas[0].Alerta.Codigo.Should().Be("DISCO-D");
        alertas[0].Alerta.Severidade.Should().Be(esperado);
        alertas[0].Alerta.Descricao.Should().Contain(@"D:\").And.Contain("backups, logs");
    }

    [Theory]
    [InlineData(@"C:\", "C")]
    [InlineData("/", "RAIZ")]
    [InlineData("/mnt/dados", "MNTDADOS")]
    public void Codigo_Da_Unidade_E_Curto_E_So_Letras_E_Digitos(string unidade, string esperado)
        => AvaliacaoAlertas.CodigoUnidade(unidade).Should().Be(esperado);

    [Fact]
    public void Emails_Nao_Enviados_Alertam_Mas_Nunca_Por_Email()
    {
        var alertas = AvaliacaoAlertas.Avaliar(new EntradasAlertas { EmailsNaoEnviadosNoCiclo = 3 }, Agora, Opcoes);

        alertas.Should().ContainSingle(a => a.Alerta.Codigo == "EMAIL-NAO-ENVIADO" && a.Alerta.Severidade == SeveridadeAlerta.Alto);
        alertas[0].EnviarPorEmail.Should().BeFalse("o e-mail falharia de novo e o alerta voltaria a cada volta");
        AvaliacaoAlertas.Avaliar(new EntradasAlertas { EmailsNaoEnviadosNoCiclo = 0 }, Agora, Opcoes).Should().BeEmpty();
    }

    [Fact]
    public void Destinatarios_Validos_Sem_Repeticao()
        => CicloAlertasOperacionais.Destinatarios(new OpcoesObservabilidade { EmailsAlerta = " ti@exemplo.com.br; dpo@exemplo.com.br ,TI@exemplo.com.br;invalido; a@@b;@x.com;x@;  " })
            .Should().Equal("ti@exemplo.com.br", "dpo@exemplo.com.br");

    // ------------------------------------------------------------------ disparo (ciclo com dublês)

    [Fact]
    public async Task Alertas_Disparam_Por_Log_Incidente_E_Email_Uma_Vez_E_Repetem_Depois_Do_Prazo()
    {
        const long Gb = 1024L * 1024 * 1024;
        var c = new Cenario();
        c.Saude.Estados = [Batida(ativo: false, Agora.AddMinutes(-9))];
        c.Discos.Itens = [new EspacoDisco(@"D:\", "backups", 80 * Gb, 1000 * Gb)];

        var primeira = await c.Ciclo().ExecutarAsync();

        primeira.Disparados.Select(a => a.Alerta.Codigo).Should().BeEquivalentTo(["WORKER-FORA-DO-AR", "DISCO-D"]);
        // 1) Log: crítico e alto como Error (vão ao Visualizador de Eventos).
        c.Log.Eventos.Should().Contain(e => e.Id == 7300 && e.Nivel == LogLevel.Error && e.Mensagem.Contains("WORKER-FORA-DO-AR", StringComparison.Ordinal));
        c.Log.Eventos.Should().Contain(e => e.Id == 7300 && e.Nivel == LogLevel.Error && e.Mensagem.Contains("DISCO-D", StringComparison.Ordinal));
        // 2) Auditoria de cada alerta; incidente técnico de Alto para cima (limiar padrão: Alto).
        c.Auditoria.Eventos.Count(e => e.Operacao == AcaoAuditoria.AlertaOperacionalDisparado).Should().Be(2);
        c.Incidentes.Itens.Should().HaveCount(2);
        var worker = c.Incidentes.Itens.Single(i => i.Codigo == "A-WORKER-FORA-DO-AR");
        worker.Tipo.Should().Be(TipoIncidente.Tecnico);
        worker.Severidade.Should().Be(SeveridadeIncidente.Critico);
        // 3) E-mail: cada alerta para cada destinatário, com o código no assunto.
        c.Fila.Mensagens.Should().HaveCount(4);
        c.Fila.Mensagens.Select(m => m.Para).Distinct().Should().BeEquivalentTo(["ti@exemplo.com.br", "dpo@exemplo.com.br"]);
        c.Fila.Mensagens.Should().Contain(m => m.Assunto.Contains("CRÍTICO", StringComparison.Ordinal) && m.Assunto.Contains("WORKER-FORA-DO-AR", StringComparison.Ordinal));
        c.Fila.Mensagens.Should().Contain(m => m.Texto.Contains("backups", StringComparison.Ordinal));

        // Cinco minutos depois, o mesmo problema: nada é disparado de novo (nem e-mail, nem nota no incidente).
        c.Relogio.AgoraUtc = Agora.AddMinutes(5);
        var segunda = await c.Ciclo().ExecutarAsync();
        segunda.Ativos.Should().HaveCount(2);
        segunda.Disparados.Should().BeEmpty();
        c.Fila.Mensagens.Should().HaveCount(4);

        // Depois do prazo de repetição (24 h), persiste ⇒ dispara de novo; o incidente aberto só ganha uma nota.
        var timeline = worker.Timeline.Count;
        c.Relogio.AgoraUtc = Agora.AddHours(25);
        c.Saude.Estados = [Batida(ativo: false, Agora.AddMinutes(-9))];
        c.Discos.Itens = [];
        var terceira = await c.Ciclo().ExecutarAsync();
        terceira.Disparados.Select(a => a.Alerta.Codigo).Should().Equal("WORKER-FORA-DO-AR");
        terceira.Normalizados.Should().Equal("DISCO-D");
        c.Incidentes.Itens.Should().HaveCount(2, "dedup: incidente aberto com o mesmo código");
        c.Incidentes.Itens.Single(i => i.Codigo == "A-WORKER-FORA-DO-AR").Timeline.Count.Should().Be(timeline + 1);
        c.Fila.Mensagens.Should().HaveCount(6);
    }

    [Fact]
    public async Task Condicao_Que_Some_Normaliza_E_Se_Voltar_Dispara_De_Novo()
    {
        var c = new Cenario();
        c.Saude.Estados = [Batida(ativo: false, Agora.AddMinutes(-9))];
        (await c.Ciclo().ExecutarAsync()).Disparados.Should().ContainSingle();

        c.Relogio.AgoraUtc = Agora.AddMinutes(5);
        c.Saude.Estados = [Batida(ativo: true, Agora.AddMinutes(5).AddSeconds(-10))];
        var normalizou = await c.Ciclo().ExecutarAsync();
        normalizou.Normalizados.Should().Equal("WORKER-FORA-DO-AR");
        c.Log.Eventos.Should().Contain(e => e.Id == 7302 && e.Mensagem.Contains("WORKER-FORA-DO-AR", StringComparison.Ordinal));

        c.Relogio.AgoraUtc = Agora.AddMinutes(30);
        c.Saude.Estados = [Batida(ativo: false, Agora.AddMinutes(29))];
        (await c.Ciclo().ExecutarAsync()).Disparados.Select(a => a.Alerta.Codigo).Should().Equal("WORKER-FORA-DO-AR");
        c.Fila.Mensagens.Should().HaveCount(4, "dois disparos para dois destinatários");
    }

    [Fact]
    public async Task Severidade_Que_Sobe_Dispara_De_Novo_Antes_Do_Prazo()
    {
        const long Gb = 1024L * 1024 * 1024;
        var c = new Cenario();
        c.Discos.Itens = [new EspacoDisco(@"D:\", "backups", 80 * Gb, 1000 * Gb)];
        (await c.Ciclo().ExecutarAsync()).Disparados.Should().ContainSingle(a => a.Alerta.Severidade == SeveridadeAlerta.Alto);

        c.Relogio.AgoraUtc = Agora.AddMinutes(5);
        c.Discos.Itens = [new EspacoDisco(@"D:\", "backups", 30 * Gb, 1000 * Gb)];
        (await c.Ciclo().ExecutarAsync()).Disparados.Should().ContainSingle(a => a.Alerta.Severidade == SeveridadeAlerta.Critico);
    }

    [Fact]
    public async Task Fonte_Com_Falha_E_Registrada_Nao_Para_As_Outras_E_Nao_Normaliza_O_Que_Estava_Ativo()
    {
        var c = new Cenario();
        c.Saude.Estados = [Batida(ativo: false, Agora.AddMinutes(-9))];
        (await c.Ciclo().ExecutarAsync()).Disparados.Should().ContainSingle();

        c.Relogio.AgoraUtc = Agora.AddMinutes(5);
        c.Saude.Falha = new TimeoutException("catálogo lento");
        c.Discos.Itens = [new EspacoDisco(@"D:\", "backups", 30L * 1024 * 1024 * 1024, 1000L * 1024 * 1024 * 1024)];
        var resultado = await c.Ciclo().ExecutarAsync();

        resultado.FontesComFalha.Should().Equal(FonteAlerta.Worker);
        resultado.Disparados.Select(a => a.Alerta.Codigo).Should().Equal("DISCO-D");
        resultado.Normalizados.Should().BeEmpty("sem ler o Worker, nada se conclui sobre ele");
        c.Log.Eventos.Should().Contain(e => e.Id == 7303 && e.Nivel == LogLevel.Warning && e.Mensagem.Contains("TimeoutException", StringComparison.Ordinal));
        c.Log.Eventos.Should().NotContain(e => e.Mensagem.Contains("catálogo lento", StringComparison.Ordinal), "só o tipo do erro vai para o log");
    }

    [Fact]
    public async Task Falha_Ao_Registrar_O_Incidente_Nao_Impede_O_Email()
    {
        var c = new Cenario();
        c.RepositorioIncidentes = new RepositorioIncidentesQueFalha();
        c.Saude.Estados = [Batida(ativo: false, Agora.AddMinutes(-9))];

        var resultado = await c.Ciclo().ExecutarAsync();

        resultado.Disparados.Should().ContainSingle();
        c.Log.Eventos.Should().Contain(e => e.Id == 7304);
        c.Fila.Mensagens.Should().HaveCount(2);
    }

    [Fact]
    public async Task Emails_Nao_Enviados_Disparam_Sem_Email_E_Normalizam_Quando_Param()
    {
        var c = new Cenario();
        c.Estatisticas.NaoEnviados = 3;
        var primeira = await c.Ciclo().ExecutarAsync();
        primeira.Disparados.Should().ContainSingle(a => a.Alerta.Codigo == "EMAIL-NAO-ENVIADO");
        c.Fila.Mensagens.Should().BeEmpty();
        c.Incidentes.Itens.Should().ContainSingle(i => i.Codigo == "A-EMAIL-NAO-ENVIADO");

        c.Relogio.AgoraUtc = Agora.AddMinutes(5);
        (await c.Ciclo().ExecutarAsync()).Normalizados.Should().Equal("EMAIL-NAO-ENVIADO");
    }

    [Fact]
    public async Task Sem_Destinatarios_Os_Alertas_Seguem_Pelo_Log_E_Pelo_Incidente()
    {
        var c = new Cenario { Opcoes = new OpcoesObservabilidade { LimiteEstagnacaoSegundos = 300 } };
        c.Saude.Estados = [Batida(ativo: false, Agora.AddMinutes(-9))];

        (await c.Ciclo().ExecutarAsync()).Disparados.Should().ContainSingle();

        c.Fila.Mensagens.Should().BeEmpty();
        c.Incidentes.Itens.Should().ContainSingle();
        c.Log.Eventos.Should().Contain(e => e.Id == 7305);
    }

    [Fact]
    public async Task Backup_Atrasado_So_Com_Agendamento_Ligado_E_Fora_Da_Carencia_De_Inicio()
    {
        var c = new Cenario { OpcoesBackup = new OpcoesBackup { AgendamentoHabilitado = true } };
        c.Saude.Estados = [Batida(true, Agora.AddSeconds(-10)), Trabalhador(ExecucaoBackupAgendado.NomeWorker, iniciado: Agora.AddHours(-3))];
        c.Backups.Existe = false;

        (await c.Ciclo().ExecutarAsync()).Disparados.Should().ContainSingle(a => a.Alerta.Codigo == "BACKUP-ATRASADO");
        c.Backups.Consultas.Should().ContainSingle();
        c.Backups.Consultas[0].Tipo.Should().Be(TipoBackup.CatalogoCompleto);
        c.Backups.Consultas[0].Desde.Should().Be(Agora.AddHours(-26));

        // Worker que acabou de subir: recupera a janela e o backup leva um tempo — não é atraso.
        var recem = new Cenario { OpcoesBackup = new OpcoesBackup { AgendamentoHabilitado = true } };
        recem.Saude.Estados = [Batida(true, Agora.AddSeconds(-10)), Trabalhador(ExecucaoBackupAgendado.NomeWorker, iniciado: Agora.AddMinutes(-30))];
        recem.Backups.Existe = false;
        (await recem.Ciclo().ExecutarAsync()).Ativos.Should().BeEmpty();

        // Agendamento desligado: o banco de backups nem é consultado.
        var desligado = new Cenario();
        desligado.Backups.Existe = false;
        (await desligado.Ciclo().ExecutarAsync()).Ativos.Should().BeEmpty();
        desligado.Backups.Consultas.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ dublês

    private static SituacaoServicoWorker Situacao(EstadoServicoWorker estado) => new(estado, "-", Agora, [], []);

    private static EstadoSaudeWorker Batida(bool ativo, DateTimeOffset ultima)
        => new(AvaliacaoServicoWorker.WorkerBatida, ativo, ultima.AddHours(-1), ultima, ultima, 10, null, null, 0, 0);

    private static EstadoSaudeWorker Trabalhador(string nome, DateTimeOffset iniciado)
        => new(nome, true, iniciado, Agora.AddSeconds(-20), null, 0, null, null, 0, 0);

    private sealed class Cenario
    {
        public RelogioMutavel Relogio { get; } = new(Agora);

        public SaudeFake Saude { get; }

        public Cenario() => Saude = new SaudeFake(Relogio);

        public BackupsFake Backups { get; } = new();

        public DiscosFake Discos { get; } = new();

        public RepositorioIncidentesFake Incidentes { get; } = new();

        /// <summary>Repositório usado pela ponte (padrão: <see cref="Incidentes"/>).</summary>
        public Application.Incidentes.IRepositorioIncidentes? RepositorioIncidentes { get; set; }

        public AuditoriaFake Auditoria { get; } = new();

        public FilaFake Fila { get; } = new();

        public EstatisticasFake Estatisticas { get; } = new();

        public EstadoAlertasNotificados Estado { get; } = new();

        public LoggerColetor Log { get; } = new();

        public OpcoesObservabilidade Opcoes { get; init; } =
            new() { LimiteEstagnacaoSegundos = 300, EmailsAlerta = "ti@exemplo.com.br;dpo@exemplo.com.br" };

        public OpcoesBackup OpcoesBackup { get; init; } = new();

        /// <summary>Um ciclo por volta (escopo novo), com o MESMO estado do processo — como no host.</summary>
        public CicloAlertasOperacionais Ciclo()
            => new(Saude, Backups, Discos, new ServicoPonteAlertaIncidente(RepositorioIncidentes ?? Incidentes, Auditoria, Relogio, Opcoes),
                Fila, Estatisticas, Estado, Relogio, Opcoes, OpcoesBackup, Log);
    }

    private sealed class RelogioMutavel(DateTimeOffset agora) : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; set; } = agora;
    }

    /// <summary>Sem estados definidos, o Worker está no ar (batida de 10 s atrás no relógio do cenário).</summary>
    private sealed class SaudeFake(RelogioMutavel relogio) : IRepositorioSaudeWorkersPersistida
    {
        public IReadOnlyList<EstadoSaudeWorker>? Estados { get; set; }

        public Exception? Falha { get; set; }

        public Task GravarAsync(IReadOnlyList<EstadoSaudeWorker> estados, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EstadoSaudeWorker>> ListarAsync(CancellationToken cancellationToken = default)
            => Falha is not null
                ? Task.FromException<IReadOnlyList<EstadoSaudeWorker>>(Falha)
                : Task.FromResult(Estados ?? [Batida(true, relogio.AgoraUtc.AddSeconds(-10))]);
    }

    private sealed class BackupsFake : IRepositorioRegistroBackup
    {
        public bool Existe { get; set; } = true;

        public List<(TipoBackup Tipo, DateTimeOffset Desde)> Consultas { get; } = [];

        public Task<bool> ExisteConcluidoOuEmAndamentoDesdeAsync(
            TipoBackup tipo, DateTimeOffset desdeUtc, DateTimeOffset emAndamentoDesdeUtc, CancellationToken cancellationToken = default)
        {
            Consultas.Add((tipo, desdeUtc));
            return Task.FromResult(Existe);
        }

        public Task AdicionarAsync(RegistroBackup registro, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AtualizarAsync(RegistroBackup registro, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RegistroBackup?> ObterAsync(Guid backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RegistroBackup>> ListarPorTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RegistroBackup>> ListarConcluidosAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> ContarConcluidosAsync(Guid? tenantId, TipoBackup tipo, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RegistroBackup>> ListarIniciadosAntesDeAsync(DateTimeOffset antesDeUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RegistroBackup>> ListarConcluidosVencidosAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RegistroBackup>> ListarRecentesAsync(int quantidade, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DiscosFake : IVerificadorEspacoDisco
    {
        public IReadOnlyList<EspacoDisco> Itens { get; set; } = [];

        public IReadOnlyList<EspacoDisco> Medir() => Itens;
    }

    private sealed class FilaFake : IFilaEmail
    {
        public List<MensagemEmail> Mensagens { get; } = [];

        public bool Habilitado => true;

        public bool Enfileirar(MensagemEmail mensagem)
        {
            Mensagens.Add(mensagem);
            return true;
        }
    }

    private sealed class EstatisticasFake : IEstatisticasFilaEmail
    {
        public long NaoEnviados { get; set; }

        public int Pendentes => 0;
    }

    private sealed class RepositorioIncidentesQueFalha : RepositorioIncidentesFakeBase
    {
        public override Task<IncidenteSeguranca?> ObterPorCodigoAsync(string codigo, CancellationToken cancellationToken = default)
            => throw new TimeoutException("catálogo fora");
    }

    /// <summary>Base que delega para o fake em memória e permite falhar um método.</summary>
    private class RepositorioIncidentesFakeBase : Application.Incidentes.IRepositorioIncidentes
    {
        private readonly RepositorioIncidentesFake _interno = new();

        public Task AdicionarAsync(IncidenteSeguranca incidente, CancellationToken cancellationToken = default) => _interno.AdicionarAsync(incidente, cancellationToken);

        public Task<IncidenteSeguranca?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) => _interno.ObterPorIdAsync(id, cancellationToken);

        public virtual Task<IncidenteSeguranca?> ObterPorCodigoAsync(string codigo, CancellationToken cancellationToken = default) => _interno.ObterPorCodigoAsync(codigo, cancellationToken);

        public Task<IReadOnlyList<IncidenteSeguranca>> ObterRecentesAsync(int limite, CancellationToken cancellationToken = default) => _interno.ObterRecentesAsync(limite, cancellationToken);

        public Task SalvarAsync(CancellationToken cancellationToken = default) => _interno.SalvarAsync(cancellationToken);
    }

    private sealed class LoggerColetor : ILogger<CicloAlertasOperacionais>
    {
        public List<(int Id, LogLevel Nivel, string Mensagem)> Eventos { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Eventos.Add((eventId.Id, logLevel, formatter(state, exception)));
    }
}
