using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Notificacoes;
using PortalBase.Infrastructure.Email;

namespace PortalBase.Integration.Tests.Email;

/// <summary>
/// E-mail transacional sem servidor: validação fail-fast da configuração (TLS e pasta local só fora de produção,
/// destinatário sem injeção de cabeçalho), fila (desligada recusa), nova tentativa controlada sem vazar o
/// destinatário no log, e o modo pasta local (.eml numa pasta privada).
/// </summary>
public sealed class ConfiguracaoEmailTests
{
    private static OpcoesEmail SmtpValido() => new()
    {
        Modo = OpcoesEmail.ModoSmtp,
        UrlPublicaPortal = "https://portal.exemplo.com.br",
        Remetente = { Email = "nao-responda@exemplo.com.br" },
        Smtp = { Servidor = "smtp.exemplo.com.br", Porta = 587, Seguranca = OpcoesSmtp.SegurancaStartTls, Usuario = "portal" },
    };

    [Fact]
    public void Smtp_Com_Tls_E_Https_E_Valido_Em_Producao()
        => ValidadorOpcoesEmail.Validar(SmtpValido(), desenvolvimento: false).Should().BeEmpty();

    [Fact]
    public void Desligado_E_Valido_Sem_Mais_Nada()
        => ValidadorOpcoesEmail.Validar(new OpcoesEmail(), desenvolvimento: false).Should().BeEmpty();

    [Theory]
    [InlineData("sem-tls")]
    [InlineData("pasta-local")]
    [InlineData("http")]
    [InlineData("url-com-query")]
    [InlineData("remetente-invalido")]
    [InlineData("servidor-vazio")]
    [InlineData("porta")]
    [InlineData("modo-desconhecido")]
    [InlineData("usuario-sem-senha-ref")]
    [InlineData("timeout")]
    public void Configuracao_Insegura_Ou_Incompleta_E_Recusada_Em_Producao(string caso)
    {
        var opcoes = SmtpValido();
        switch (caso)
        {
            case "sem-tls": opcoes.Smtp.Seguranca = OpcoesSmtp.SegurancaNenhuma; break;
            case "pasta-local": opcoes.Modo = OpcoesEmail.ModoPastaLocal; opcoes.PastaLocal.Caminho = Path.GetTempPath(); break;
            case "http": opcoes.UrlPublicaPortal = "http://portal.exemplo.com.br"; break;
            case "url-com-query": opcoes.UrlPublicaPortal = "https://portal.exemplo.com.br/?x=1"; break;
            case "remetente-invalido": opcoes.Remetente.Email = "nao-responda@exemplo.com.br\r\nBcc: alvo@x.com"; break;
            case "servidor-vazio": opcoes.Smtp.Servidor = " "; break;
            case "porta": opcoes.Smtp.Porta = 0; break;
            case "modo-desconhecido": opcoes.Modo = "smtp"; break;
            case "usuario-sem-senha-ref": opcoes.Smtp.SenhaRef = ""; break;
            case "timeout": opcoes.Smtp.TimeoutSegundos = 1; break;
        }

        ValidadorOpcoesEmail.Validar(opcoes, desenvolvimento: false).Should().NotBeEmpty();
    }

    [Fact]
    public void Em_Development_Smtp_Sem_Tls_E_Pasta_Local_Sao_Aceitos()
    {
        var semTls = SmtpValido();
        semTls.Smtp.Seguranca = OpcoesSmtp.SegurancaNenhuma;
        semTls.UrlPublicaPortal = "http://localhost:5251";
        ValidadorOpcoesEmail.Validar(semTls, desenvolvimento: true).Should().BeEmpty();

        var pasta = new OpcoesEmail
        {
            Modo = OpcoesEmail.ModoPastaLocal,
            UrlPublicaPortal = "http://localhost:5251",
            Remetente = { Email = "portal@localhost.dev" },
            PastaLocal = { Caminho = Path.Combine(Path.GetTempPath(), "emails") },
        };
        ValidadorOpcoesEmail.Validar(pasta, desenvolvimento: true).Should().BeEmpty();
    }

    [Fact]
    public void Configuracao_Invalida_Falha_O_Startup_Sem_Expor_Valores()
    {
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Modo"] = "Smtp",
            ["Email:UrlPublicaPortal"] = "http://portal",
            ["Email:Remetente:Email"] = "x@y.com",
            ["Email:Smtp:Servidor"] = "smtp.y.com",
            ["Email:Smtp:Seguranca"] = "Nenhuma",
        }).Build();

        var acao = () => new ServiceCollection().AddEmail(configuracao, desenvolvimento: false);

        acao.Should().Throw<InvalidOperationException>()
            .WithMessage("*Email:Smtp:Seguranca=Nenhuma*").And.Message.Should().NotContain("smtp.y.com");
    }

    [Theory]
    [InlineData("alvo@exemplo.com\r\nBcc: outro@exemplo.com")]
    [InlineData("a@exemplo.com, b@exemplo.com")]
    [InlineData("Nome <a@exemplo.com>")]
    [InlineData("sem-arroba")]
    [InlineData("")]
    public void Destinatario_Fora_Do_Formato_Simples_E_Recusado(string para)
        => ValidadorOpcoesEmail.EnderecoValido(para).Should().BeFalse();

    [Fact]
    public void Fila_Desligada_Recusa_E_Nao_Enfileira()
    {
        var fila = new FilaEmailEmMemoria(Options.Create(new OpcoesEmail()), NullLogger<FilaEmailEmMemoria>.Instance);

        fila.Habilitado.Should().BeFalse();
        fila.Enfileirar(new MensagemEmail("a@exemplo.com", "Assunto", "Texto")).Should().BeFalse();
        fila.Leitor.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Nova_Tentativa_Ate_Enviar_E_Log_Sem_Destinatario_Nem_Conteudo()
    {
        var (processador, enviador, logs) = Processador(falhasAntesDeEnviar: 2, tentativas: 3);
        var mensagem = new MensagemEmail("destino.secreto@exemplo.com", "Assunto", "link-de-uso-unico");

        (await processador.EnviarComNovaTentativaAsync(mensagem, CancellationToken.None)).Should().BeTrue();

        enviador.Chamadas.Should().Be(3);
        enviador.Enviadas.Should().ContainSingle();
        logs.Texto.Should().NotContain("destino.secreto").And.NotContain("link-de-uso-unico");
    }

    [Fact]
    public async Task Falha_Final_Depois_Do_Limite_De_Tentativas()
    {
        var (processador, enviador, logs) = Processador(falhasAntesDeEnviar: 10, tentativas: 3);

        (await processador.EnviarComNovaTentativaAsync(new MensagemEmail("x@exemplo.com", "A", "T"), CancellationToken.None)).Should().BeFalse();

        enviador.Chamadas.Should().Be(3);
        logs.Texto.Should().Contain("NÃO enviado após 3").And.NotContain("x@exemplo.com");
    }

    [Fact]
    public async Task Pasta_Local_Grava_Eml_Numa_Pasta_Privada()
    {
        var pasta = Path.Combine(Path.GetTempPath(), "portalbase-emails-" + Guid.NewGuid().ToString("N"));
        try
        {
            var opcoes = Options.Create(new OpcoesEmail
            {
                Modo = OpcoesEmail.ModoPastaLocal,
                Remetente = { Email = "portal@localhost.dev", Nome = "Portal Base" },
                PastaLocal = { Caminho = pasta },
            });
            var enviador = new EnviadorEmailPastaLocal(opcoes, new RelogioFixo());

            await enviador.EnviarAsync(new MensagemEmail("Maria@Exemplo.com", "Redefinição de senha", "Abra o link: https://x/y", "<p>Abra</p>"));

            var arquivo = Directory.GetFiles(pasta, "*.eml").Should().ContainSingle().Subject;
            Directory.GetFiles(pasta).Should().ContainSingle("grava com outro nome e renomeia no fim — nada pela metade fica na pasta");
            var mime = await MimeKit.MimeMessage.LoadAsync(arquivo);
            mime.To.Mailboxes.Should().ContainSingle().Which.Address.Should().Be("Maria@Exemplo.com");
            mime.From.Mailboxes.Should().ContainSingle().Which.Address.Should().Be("portal@localhost.dev");
            mime.Subject.Should().Be("Redefinição de senha");
            mime.TextBody.Should().Contain("https://x/y");
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(pasta).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            if (Directory.Exists(pasta))
            {
                Directory.Delete(pasta, recursive: true);
            }
        }
    }

    private static (ProcessadorFilaEmail Processador, EnviadorFalho Enviador, LogsCapturados Logs) Processador(int falhasAntesDeEnviar, int tentativas)
    {
        var enviador = new EnviadorFalho(falhasAntesDeEnviar);
        var servicos = new ServiceCollection().AddSingleton<IEnviadorEmail>(enviador).BuildServiceProvider();
        var opcoes = Options.Create(new OpcoesEmail { Modo = OpcoesEmail.ModoSmtp, Smtp = { TentativasMaximas = tentativas } });
        var logs = new LogsCapturados();
        var fila = new FilaEmailEmMemoria(opcoes, NullLogger<FilaEmailEmMemoria>.Instance);
        var processador = new ProcessadorFilaEmail(
            fila, servicos.GetRequiredService<IServiceScopeFactory>(), opcoes, logs.CriarLogger<ProcessadorFilaEmail>(), (_, _) => Task.CompletedTask);
        return (processador, enviador, logs);
    }

    private sealed class EnviadorFalho(int falhas) : IEnviadorEmail
    {
        public int Chamadas { get; private set; }

        public List<MensagemEmail> Enviadas { get; } = [];

        public Task EnviarAsync(MensagemEmail mensagem, CancellationToken cancellationToken = default)
        {
            Chamadas++;
            if (Chamadas <= falhas)
            {
                throw new IOException("falha simulada para destino.secreto@exemplo.com");
            }

            Enviadas.Add(mensagem);
            return Task.CompletedTask;
        }
    }

    private sealed class RelogioFixo : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    }

    internal sealed class LogsCapturados
    {
        private readonly List<string> _linhas = [];

        public string Texto
        {
            get
            {
                lock (_linhas)
                {
                    return string.Join('\n', _linhas);
                }
            }
        }

        public ILogger<T> CriarLogger<T>() => new Logger<T>(this);

        private sealed class Logger<T>(LogsCapturados destino) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (destino._linhas)
                {
                    destino._linhas.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
                }
            }
        }
    }
}
