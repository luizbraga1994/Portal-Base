using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Backup;
using PortalBase.Application.Notificacoes;
using PortalBase.Application.Observabilidade;
using PortalBase.Domain.Incidentes;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Observabilidade;

/// <summary>
/// OBS-004 (auditoria de 08/10/2026) — "alertas disparam em teste" contra o CATÁLOGO REAL (MySQL): Worker parado
/// (batida gravada) e disco com pouco espaço viram, numa volta da rotina, log de erro, auditoria do alerta, incidente
/// técnico aberto e e-mail na fila. Antes, nenhuma dessas condições avisava ninguém.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class AlertasOperacionaisMySqlTests
{
    private readonly FixtureMultiTenant _fixture;

    public AlertasOperacionaisMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Volta_Da_Rotina_Dispara_Auditoria_Incidente_E_Email_A_Partir_Do_Catalogo()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var agora = DateTimeOffset.UtcNow;

        // Worker parado: o desligamento gravou Ativo = false há 9 min.
        using (var escopo = _fixture.CriarEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>().SaudeWorkers.ExecuteDeleteAsync();
            await escopo.ServiceProvider.GetRequiredService<IRepositorioSaudeWorkersPersistida>().GravarAsync(
                [new EstadoSaudeWorker(AvaliacaoServicoWorker.WorkerBatida, false, agora.AddHours(-2), agora.AddMinutes(-9), agora.AddMinutes(-9), 10, null, null, 0, 0)],
                agora.AddMinutes(-9));
        }


        var correlacao = Guid.NewGuid().ToString("N");
        var fila = new FilaColetora();
        var log = new LogColetor();
        ResultadoCicloAlertas resultado;
        using (var escopo = _fixture.CriarEscopo())
        {
            var sp = escopo.ServiceProvider;
            var auditoria = sp.GetRequiredService<IContextoAuditoriaMutavel>();
            auditoria.DefinirOrigem("Web", TipoAtor.JobBackground, "alertas");
            auditoria.DefinirCorrelacao(correlacao, null);
            var opcoes = new OpcoesObservabilidade { LimiteEstagnacaoSegundos = 300, EmailsAlerta = "ti@exemplo.com.br" };
            var ciclo = new CicloAlertasOperacionais(
                sp.GetRequiredService<IRepositorioSaudeWorkersPersistida>(),
                sp.GetRequiredService<IRepositorioRegistroBackup>(),
                new DiscoComPoucoEspaco(),
                sp.GetRequiredService<ServicoPonteAlertaIncidente>(),
                fila,
                new SemFalhasDeEmail(),
                new EstadoAlertasNotificados(),
                sp.GetRequiredService<IRelogio>(),
                opcoes,
                new OpcoesBackup(),
                log);
            resultado = await ciclo.ExecutarAsync();
        }

        const string codigoDisco = "DISCO-D";
        resultado.FontesComFalha.Should().BeEmpty();
        resultado.Disparados.Select(x => x.Alerta.Codigo).Should().Contain(["WORKER-FORA-DO-AR", codigoDisco]);
        resultado.Disparados.Single(x => x.Alerta.Codigo == codigoDisco).Alerta.Severidade.Should().Be(SeveridadeAlerta.Alto);

        // Log (Visualizador de Eventos) e e-mail.
        log.Eventos.Should().Contain(e => e.Nivel == LogLevel.Error && e.Mensagem.Contains("WORKER-FORA-DO-AR", StringComparison.Ordinal));
        fila.Mensagens.Should().Contain(m => m.Para == "ti@exemplo.com.br" && m.Assunto.Contains("WORKER-FORA-DO-AR", StringComparison.Ordinal));
        fila.Mensagens.Should().Contain(m => m.Assunto.Contains(codigoDisco, StringComparison.Ordinal));

        // Gravado no catálogo: auditoria de cada alerta (com a correlação da volta) e incidentes técnicos abertos.
        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var auditados = await catalogo.RegistrosAuditoria.AsNoTracking()
                .Where(r => r.Operacao == AcaoAuditoria.AlertaOperacionalDisparado && r.CorrelationId == correlacao)
                .Select(r => r.RecursoId)
                .ToListAsync();
            auditados.Should().Contain(["WORKER-FORA-DO-AR", codigoDisco]);

            var incidentes = await catalogo.IncidentesSeguranca.AsNoTracking()
                .Where(i => i.Codigo == "A-WORKER-FORA-DO-AR" || i.Codigo == "A-" + codigoDisco)
                .ToListAsync();
            incidentes = [.. incidentes.Where(i => !i.EstaEncerrado)];
            incidentes.Select(i => i.Codigo).Should().Contain(["A-WORKER-FORA-DO-AR", "A-" + codigoDisco]);
            incidentes.Should().OnlyContain(i => i.Tipo == TipoIncidente.Tecnico);
            incidentes.First(i => i.Codigo == "A-WORKER-FORA-DO-AR").Severidade.Should().Be(SeveridadeIncidente.Critico);
        }
    }

    private sealed class DiscoComPoucoEspaco : IVerificadorEspacoDisco
    {
        private const long Gb = 1024L * 1024 * 1024;

        public IReadOnlyList<EspacoDisco> Medir() => [new EspacoDisco(@"D:\", "backups", 80 * Gb, 1000 * Gb)];
    }

    private sealed class SemFalhasDeEmail : IEstatisticasFilaEmail
    {
        public long NaoEnviados => 0;

        public int Pendentes => 0;
    }

    private sealed class FilaColetora : IFilaEmail
    {
        public List<MensagemEmail> Mensagens { get; } = [];

        public bool Habilitado => true;

        public bool Enfileirar(MensagemEmail mensagem)
        {
            Mensagens.Add(mensagem);
            return true;
        }
    }

    private sealed class LogColetor : ILogger<CicloAlertasOperacionais>
    {
        public List<(LogLevel Nivel, string Mensagem)> Eventos { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Eventos.Add((logLevel, formatter(state, exception)));
    }
}
