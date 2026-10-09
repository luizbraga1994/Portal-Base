using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PortalBase.Infrastructure.Observabilidade;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;

namespace PortalBase.Integration.Tests.Observabilidade;

/// <summary>
/// Servidor próprio (Worker como serviço do Windows, Web no IIS): avisos e erros vão para o Log de Aplicativo do Windows.
/// Só avisos/erros; texto redigido (regra 8) e limitado ao tamanho aceito pelo Windows; recusa do Windows nunca derruba
/// o processo; desligado por padrão (só o script de publicação liga).
/// </summary>
public sealed class LogEventosWindowsTests
{
    private sealed class GravadorFake : IGravadorLogEventosWindows
    {
        public List<(TipoEventoWindows Tipo, int Id, string Texto)> Entradas { get; } = [];
        public Exception? Falha { get; set; }

        public void Gravar(TipoEventoWindows tipo, int idEvento, string texto)
        {
            if (Falha is not null)
            {
                throw Falha;
            }

            Entradas.Add((tipo, idEvento, texto));
        }
    }

    private static LogEvent Evento(LogEventLevel nivel, string modelo, Exception? excecao = null, params LogEventProperty[] propriedades)
        => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), nivel, excecao, new MessageTemplateParser().Parse(modelo), propriedades);

    [Theory]
    [InlineData(LogEventLevel.Verbose)]
    [InlineData(LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Information)]
    public void Abaixo_De_Aviso_Nao_Vai_Para_O_Windows(LogEventLevel nivel)
    {
        var gravador = new GravadorFake();

        new SinkLogEventosWindows(gravador).Emit(Evento(nivel, "Ciclo concluído"));

        gravador.Entradas.Should().BeEmpty("o Log de Aplicativo não pode ser inundado com informação de rotina");
    }

    [Fact]
    public void Nivel_Minimo_Abaixo_De_Aviso_E_Ignorado()
    {
        var gravador = new GravadorFake();

        new SinkLogEventosWindows(gravador, LogEventLevel.Information).Emit(Evento(LogEventLevel.Information, "Ciclo concluído"));

        gravador.Entradas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(LogEventLevel.Warning, TipoEventoWindows.Aviso, SinkLogEventosWindows.IdAviso)]
    [InlineData(LogEventLevel.Error, TipoEventoWindows.Erro, SinkLogEventosWindows.IdErro)]
    [InlineData(LogEventLevel.Fatal, TipoEventoWindows.Erro, SinkLogEventosWindows.IdFatal)]
    public void Aviso_E_Erro_Vao_Com_Tipo_E_Id(LogEventLevel nivel, TipoEventoWindows tipo, int id)
    {
        var gravador = new GravadorFake();

        new SinkLogEventosWindows(gravador).Emit(Evento(nivel, "Lote {Lote} falhou",
            null, new LogEventProperty("Lote", new ScalarValue(42)), new LogEventProperty("SourceContext", new ScalarValue("PortalBase.Worker.TrabalhadorOutbox"))));

        var entrada = gravador.Entradas.Should().ContainSingle().Subject;
        entrada.Tipo.Should().Be(tipo);
        entrada.Id.Should().Be(id);
        entrada.Texto.Should().Be("[PortalBase.Worker.TrabalhadorOutbox] Lote 42 falhou");
    }

    [Fact]
    public void Excecao_Vai_Junto_E_Valores_Sensiveis_Saem_Mascarados()
    {
        var gravador = new GravadorFake();
        var excecao = new InvalidOperationException("Falha ao conectar: Server=db;User Id=app;Password=Segredo#1;");

        new SinkLogEventosWindows(gravador).Emit(Evento(LogEventLevel.Error, "Link {Url}", excecao,
            new LogEventProperty("Url", new ScalarValue("/conta/redefinir-senha?token=uso-unico-abc"))));

        var texto = gravador.Entradas.Should().ContainSingle().Subject.Texto;
        texto.Should().Contain("InvalidOperationException").And.Contain("Password=***").And.Contain("?token=***");
        texto.Should().NotContain("Segredo#1").And.NotContain("uso-unico-abc");
    }

    [Fact]
    public void Texto_Grande_E_Cortado_No_Limite_Do_Windows()
    {
        var gravador = new GravadorFake();

        new SinkLogEventosWindows(gravador).Emit(Evento(LogEventLevel.Error, "{Detalhe}", null,
            new LogEventProperty("Detalhe", new ScalarValue(new string('x', 50_000)))));

        var texto = gravador.Entradas.Should().ContainSingle().Subject.Texto;
        texto.Length.Should().BeLessThan(31_839, "o Windows recusa entradas acima de 31.839 caracteres");
        texto.Should().EndWith("[... texto cortado; o restante está no log do servidor]");
    }

    [Fact]
    public void Recusa_Do_Windows_Nao_Derruba_E_Desliga_O_Destino()
    {
        var gravador = new GravadorFake { Falha = new System.Security.SecurityException("origem não registrada") };
        var sink = new SinkLogEventosWindows(gravador);

        var emitir = () => sink.Emit(Evento(LogEventLevel.Error, "Falhou"));

        emitir.Should().NotThrow("um destino de log nunca pode parar o Worker nem a Web");
        sink.Desligado.Should().BeTrue();

        gravador.Falha = null;
        sink.Emit(Evento(LogEventLevel.Error, "Falhou de novo"));
        gravador.Entradas.Should().BeEmpty("depois da recusa o destino fica desligado até o processo reiniciar");
    }

    [Fact]
    public void Falha_Fatal_Da_Inicializacao_Vai_Com_A_Causa()
    {
        var gravador = new GravadorFake();
        var causa = new InvalidOperationException("Configuração de produção incompleta — startup abortado (fail-fast). Faltando: Vault:Modo (x).");

        LogEventosWindows.RegistrarFalhaFatal(gravador, causa, "Falha fatal na inicialização do Worker {sem modelo}.");

        var entrada = gravador.Entradas.Should().ContainSingle().Subject;
        entrada.Tipo.Should().Be(TipoEventoWindows.Erro);
        entrada.Id.Should().Be(SinkLogEventosWindows.IdFatal);
        entrada.Texto.Should().StartWith("Falha fatal na inicialização do Worker {sem modelo}.").And.Contain("Faltando: Vault:Modo");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Desligado_Por_Padrao(string? valor)
    {
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [LogEventosWindows.ChaveHabilitado] = valor })
            .Build();

        LogEventosWindows.Habilitado(configuracao).Should().BeFalse();
    }

    [Fact]
    public void Ligado_So_No_Windows()
    {
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [LogEventosWindows.ChaveHabilitado] = "true" })
            .Build();

        LogEventosWindows.Habilitado(configuracao).Should().Be(OperatingSystem.IsWindows());
    }

    [Fact]
    public void Desligado_Nao_Acrescenta_Destino()
    {
        var configuracao = new ConfigurationBuilder().Build();
        var log = new LoggerConfiguration();

        log.GravarNoLogEventosWindowsSeHabilitado(configuracao, "PortalBase.Worker").Should().BeSameAs(log);
        using var logger = log.CreateLogger();
        var registrar = () => logger.Error("Falhou");
        registrar.Should().NotThrow();
    }
}
