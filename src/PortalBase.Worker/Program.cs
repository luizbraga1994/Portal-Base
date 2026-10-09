using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;
using PortalBase.Infrastructure.Observabilidade;
using PortalBase.Worker;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

// Depois do Build, o log configurado (com o Log de Aplicativo do Windows, se ligado) já registra a falha.
var logEventosWindowsAtivo = false;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Servidor próprio (Windows/IIS): o Worker roda como serviço do Windows. Fora de um serviço não tem efeito.
    builder.Services.AddWindowsService(opcoes => opcoes.ServiceName = ComposicaoWorker.NomeAplicacao);

    ComposicaoWorker.Configurar(builder);

    var host = builder.Build();
    logEventosWindowsAtivo = LogEventosWindows.Habilitado(builder.Configuration);

    // Instancia as métricas operacionais (Prompt 31) no arranque para criar o Meter e os gauges observáveis
    // (saúde de workers / última integração) — sem custo por probe; cardinalidade baixa (§8/§9).
    host.Services.GetRequiredService<PortalBase.Infrastructure.Observabilidade.MetricasOperacionais>();

    // Identidade da implantação (Prompt 53, Fase 3): VERSION → COMMIT → BUILD → ENV no startup (sem segredo).
    var implantacaoWorker = PortalBase.Infrastructure.Producao.InformacaoImplantacao.Coletar(builder.Environment.EnvironmentName);
    Log.Information(
        "Portal Base Worker iniciando. versao={Versao} commit={Commit} build={Build} ambiente={Ambiente}",
        implantacaoWorker.Versao, implantacaoWorker.CommitSha, implantacaoWorker.BuildTimestamp, implantacaoWorker.Ambiente);

    host.Run();
}
catch (Exception excecao)
{
    Log.Fatal(excecao, "Falha fatal na inicialização do Worker.");

    // Serviço do Windows não tem console: a causa (ex.: configuração de produção incompleta) vai para o Visualizador de
    // Eventos > Aplicativo, origem "PortalBase.Worker" — o log configurado ainda não existia neste ponto.
    if (!logEventosWindowsAtivo && WindowsServiceHelpers.IsWindowsService())
    {
        LogEventosWindows.RegistrarFalhaFatal(ComposicaoWorker.NomeAplicacao, excecao, "Falha fatal na inicialização do Worker.");
    }

    throw;
}
finally
{
    Log.CloseAndFlush();
}
