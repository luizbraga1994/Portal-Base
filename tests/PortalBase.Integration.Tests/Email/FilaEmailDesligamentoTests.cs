using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PortalBase.Application.Notificacoes;
using PortalBase.Infrastructure.Email;

namespace PortalBase.Integration.Tests.Email;

/// <summary>
/// FILA-001 (auditoria de 08/10/2026) — a fila de e-mail é em memória por decisão de segurança (o conteúdo pode ter um
/// link de uso único: não vai para banco nem log — regra 7/8). O problema era a perda SILENCIOSA: ao reciclar o pool ou
/// parar o site, o que estava na fila sumia sem registro.
/// </summary>
public sealed class FilaEmailDesligamentoTests
{
    [Fact]
    public async Task Pendentes_No_Desligamento_Sao_Enviados_Antes_De_Encerrar()
    {
        var (fila, processador, enviador, _) = Montar(new EnviadorRegistrador());
        for (var i = 1; i <= 3; i++)
        {
            fila.Enfileirar(new MensagemEmail($"pessoa{i}@exemplo.com", "Assunto", "link-de-uso-unico")).Should().BeTrue();
        }

        using var desligamento = new CancellationTokenSource();
        await desligamento.CancelAsync(); // o host pediu para parar antes de o processador ler a fila

        await processador.ProcessarAsync(desligamento.Token);

        enviador.Enviadas.Should().HaveCount(3, "o desligamento esvazia a fila dentro de um prazo curto");
        fila.Pendentes.Should().Be(0);
        fila.NaoEnviados.Should().Be(0);
    }

    [Fact]
    public async Task Sem_Tempo_No_Desligamento_O_Que_Sobra_E_Contado_E_Registrado_Sem_Destinatario()
    {
        var (fila, processador, _, logs) = Montar(new EnviadorTravado(), TimeSpan.FromMilliseconds(300));
        for (var i = 1; i <= 3; i++)
        {
            fila.Enfileirar(new MensagemEmail($"pessoa{i}@exemplo.com", "Assunto", "link-de-uso-unico")).Should().BeTrue();
        }

        using var desligamento = new CancellationTokenSource();
        await desligamento.CancelAsync();
        var relogio = System.Diagnostics.Stopwatch.StartNew();

        await processador.ProcessarAsync(desligamento.Token);

        relogio.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "o desligamento não fica preso no servidor de e-mail");
        fila.NaoEnviados.Should().Be(3, "os três ficam contados para a rotina de alertas (EMAIL-NAO-ENVIADO)");
        logs.Texto.Should().Contain("3 e-mail(s) da fila NÃO enviados");
        logs.Texto.Should().NotContain("pessoa1@exemplo.com").And.NotContain("link-de-uso-unico");
    }

    [Fact]
    public async Task Fila_Cheia_E_Falha_Final_Contam_Como_Nao_Enviados()
    {
        var (fila, processador, _, _) = Montar(new EnviadorQueFalha());
        for (var i = 0; i < FilaEmailEmMemoria.Capacidade; i++)
        {
            fila.Enfileirar(new MensagemEmail("a@exemplo.com", "A", "T")).Should().BeTrue();
        }

        fila.Enfileirar(new MensagemEmail("a@exemplo.com", "A", "T")).Should().BeFalse("fila cheia");
        fila.NaoEnviados.Should().Be(1);
        fila.Pendentes.Should().Be(FilaEmailEmMemoria.Capacidade);

        (await processador.EnviarComNovaTentativaAsync(new MensagemEmail("b@exemplo.com", "B", "T"), CancellationToken.None)).Should().BeFalse();
        fila.NaoEnviados.Should().Be(2);
    }

    private static (FilaEmailEmMemoria Fila, ProcessadorFilaEmail Processador, T Enviador, ConfiguracaoEmailTests.LogsCapturados Logs) Montar<T>(
        T enviador, TimeSpan? prazoDesligamento = null)
        where T : class, IEnviadorEmail
    {
        var servicos = new ServiceCollection().AddSingleton<IEnviadorEmail>(enviador).BuildServiceProvider();
        var opcoes = Options.Create(new OpcoesEmail { Modo = OpcoesEmail.ModoSmtp, Smtp = { TentativasMaximas = 3 } });
        var logs = new ConfiguracaoEmailTests.LogsCapturados();
        var fila = new FilaEmailEmMemoria(opcoes, NullLogger<FilaEmailEmMemoria>.Instance);
        var processador = new ProcessadorFilaEmail(
            fila, servicos.GetRequiredService<IServiceScopeFactory>(), opcoes, logs.CriarLogger<ProcessadorFilaEmail>(), (_, _) => Task.CompletedTask, prazoDesligamento);
        return (fila, processador, enviador, logs);
    }

    private sealed class EnviadorRegistrador : IEnviadorEmail
    {
        public List<MensagemEmail> Enviadas { get; } = [];

        public Task EnviarAsync(MensagemEmail mensagem, CancellationToken cancellationToken = default)
        {
            lock (Enviadas)
            {
                Enviadas.Add(mensagem);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Servidor de e-mail que não responde: só termina quando o prazo cancela.</summary>
    private sealed class EnviadorTravado : IEnviadorEmail
    {
        public Task EnviarAsync(MensagemEmail mensagem, CancellationToken cancellationToken = default)
            => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class EnviadorQueFalha : IEnviadorEmail
    {
        public Task EnviarAsync(MensagemEmail mensagem, CancellationToken cancellationToken = default)
            => throw new IOException("servidor de e-mail recusou");
    }
}
