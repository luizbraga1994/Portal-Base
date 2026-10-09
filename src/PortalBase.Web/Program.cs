using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PortalBase.Application.Auditoria;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PortalBase.Application.DependencyInjection;
using PortalBase.Infrastructure.DependencyInjection;
using PortalBase.Infrastructure.Email;
using PortalBase.Infrastructure.Saude;
using PortalBase.Web.Components;
using PortalBase.Infrastructure.Observabilidade;
using PortalBase.Web.Seguranca;
using PortalBase.Web.Tarefas;
using PortalBase.Web.Ui.DependencyInjection;
using Serilog;
using Serilog.Context;

// Logger de bootstrap: captura falhas antes do host subir (regra 12/1).
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    const string NomeAplicacao = "PortalBase.Web";

    // Servidor próprio (IIS): a configuração (sem segredo) gravada pelo scripts/configurar-iis.ps1 em
    // %ProgramData%\PortalBase\PortalBase.Web.json vale para o site e para o .exe aberto à mão. Só em produção.
    PortalBase.Infrastructure.Producao.ConfiguracaoServidor.Adicionar(builder.Configuration, builder.Environment.EnvironmentName, NomeAplicacao);

    // Development: o que a tela grava no "cofre" local (segredos de integração, senha do SMTP) vai também para o user-secrets
    // desta aplicação (perfil do usuário, fora do repositório) e sobrevive ao reinício. Produção usa o cofre gerenciado.
    if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["Vault:ArquivoSegredosLocal"]))
    {
        var idUserSecrets = typeof(Program).Assembly
            .GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute), inherit: false)
            .OfType<Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute>()
            .FirstOrDefault()?.UserSecretsId;
        if (idUserSecrets is not null)
        {
            builder.Configuration.AddInMemoryCollection(
            [
                new("Vault:ArquivoSegredosLocal", Microsoft.Extensions.Configuration.UserSecrets.PathHelper.GetSecretsPathFromSecretsId(idUserSecrets)),
            ]);
        }
    }

    // Recuperação de desastre: "PortalBase.Web.exe decifrar-backup ..." decifra um artefato de backup e sai — sem banco e
    // sem subir o site (o catálogo pode ser justamente o que se perdeu). Ver docs/runbooks/DISASTER-RECOVERY.md.
    if (PortalBase.Web.Hospedagem.ComandoDecifrarBackup.Solicitado(args))
    {
        Environment.ExitCode = await PortalBase.Web.Hospedagem.ComandoDecifrarBackup.ExecutarAsync(args, builder.Configuration, builder.Environment);
        return;
    }

    // ---------- Endurecimento do servidor (Prompt 07) ----------
    // Não expor o cabeçalho Server (information disclosure — seção 24) e limitar o corpo da
    // requisição (baseline de upload — seção 16; ajustável por configuração).
    var maxCorpoBytes = builder.Configuration.GetValue<long?>("Seguranca:MaxTamanhoRequisicaoBytes") ?? 20L * 1024 * 1024;
    builder.WebHost.ConfigureKestrel(opcoes =>
    {
        opcoes.AddServerHeader = false;
        opcoes.Limits.MaxRequestBodySize = maxCorpoBytes;
    });

    // HSTS explícito para produção (seção 4): 1 ano, incluindo subdomínios. Preload só se e
    // quando o domínio estiver realmente inscrito na lista (decisão de infraestrutura).
    builder.Services.AddHsts(opcoes =>
    {
        opcoes.MaxAge = TimeSpan.FromDays(365);
        opcoes.IncludeSubDomains = true;
        opcoes.Preload = false;
    });

    // ---------- Observabilidade ----------
    builder.Host.UseSerilog((contexto, servicos, configuracao) => configuracao
        .ReadFrom.Configuration(contexto.Configuration)
        .ReadFrom.Services(servicos)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", NomeAplicacao)
        .Enrich.With(new EnriquecedorRedacaoLogs()) // regra 8: ?token=... e afins saem mascarados
        // No IIS não há console: avisos e erros vão também para o Log de Aplicativo do Windows (origem "PortalBase.Web"),
        // ligado pelo scripts/configurar-iis.ps1 (Observabilidade:LogEventosWindows).
        .GravarNoLogEventosWindowsSeHabilitado(contexto.Configuration, NomeAplicacao)
        // Information em diante num arquivo por dia (OBS-001): sem console no IIS/serviço, era o único registro perdido.
        .GravarEmArquivoSeConfigurado(contexto.Configuration, NomeAplicacao));

    var otlpConfigurado = !string.IsNullOrWhiteSpace(
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(recurso => recurso.AddService(NomeAplicacao))
        .WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation();
            tracing.AddSource(PortalBase.Infrastructure.Observabilidade.FonteAtividadePortal.NomeFonte); // spans próprios (Prompt 51)
            if (otlpConfigurado)
            {
                tracing.AddOtlpExporter();
            }
        })
        .WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation();
            metrics.AddMeter(PortalBase.Infrastructure.Provisionamento.MetricasProvisionamento.NomeMeter);
            metrics.AddMeter(PortalBase.Infrastructure.Observabilidade.MetricasOperacionais.NomeMeter); // Prompt 31
            if (otlpConfigurado)
            {
                metrics.AddOtlpExporter();
            }
        });

    // ---------- Pré-checagem de produção (Prompt 33, §9/§10) ----------
    // FAIL-FAST: em ambiente produtivo, aborta o startup se faltar configuração essencial
    // (catálogo, cofre gerenciado, armazenamento).
    // Nunca lê valores de segredo — só presença. Em Dev/Test/Staging é no-op.
    PortalBase.Infrastructure.Producao.PreflightProducao.Exigir(
        builder.Configuration, builder.Environment.EnvironmentName, PortalBase.Infrastructure.Producao.ConfiguracaoServidor.Dica(NomeAplicacao));

    // ---------- Composição de DI (camadas) ----------
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(
        builder.Configuration,
        conexaoObrigatoria: builder.Environment.IsProduction(),
        producao: builder.Environment.IsProduction());

    // ---------- E-mail transacional (redefinição de senha) ----------
    // Email:Modo = Smtp | PastaLocal (só Development/Testing) | Desligado. Configuração inválida falha o startup;
    // a senha do SMTP vem do cofre. O envio é feito em segundo plano, com nova tentativa controlada.
    builder.Services.AddEmail(builder.Configuration, desenvolvimento: builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));
    builder.Services.AddHostedService<PortalBase.Web.Email.DisparadorEmail>();

    // ---------- Alertas operacionais (OBS-004) ----------
    // A cada 5 min confere Worker, backup, discos e e-mails não enviados; dispara Visualizador de
    // Eventos + auditoria/incidente + e-mail (Observabilidade:EmailsAlerta). Na Web: é ela que envia e-mail e enxerga
    // o Worker parado. Nos testes automatizados (Testing) o ciclo é chamado direto, sem disparo em segundo plano.
    builder.Services.AddSingleton<PortalBase.Application.Observabilidade.EstadoAlertasNotificados>();
    builder.Services.AddScoped<PortalBase.Application.Observabilidade.CicloAlertasOperacionais>();
    if (!builder.Environment.IsEnvironment("Testing"))
    {
        builder.Services.AddHostedService<PortalBase.Web.Observabilidade.MonitorAlertas>();
    }

    // ---------- Tarefas em segundo plano (ARQ-006) ----------
    // Trabalho pesado roda FORA do circuito: a tela enfileira no catálogo e este processo executa
    // (com a identidade de quem pediu). Tarefas:ExecutorHabilitado desliga o laço (ex.: outro nó executa).
    builder.Services.AddExecutorTarefas(ServicosTarefasWeb.ExecutorHabilitado(builder.Configuration, builder.Environment));

    // ---------- Data Protection (Prompt 11) ----------
    // Chaveiro de sessão (cookie de auth + antiforgery): nome de aplicação estável e persistência
    // opcional em volume (produção). Fail-closed se o caminho configurado for inutilizável.
    builder.Services.AddDataProtectionEndurecido(builder.Configuration);

    // ---------- Identidade / autorização (Prompt 04) ----------
    builder.Services.AddSegurancaIdentidade(builder.Environment, builder.Configuration);

    // ---------- Apresentação / UI (Prompt 35) ----------
    // Serviços de apresentação (navegação RBAC, contexto, notificações). Sem regra de negócio.
    builder.Services.AddUiPortalBase();

    // Forwarded headers: só confia em X-Forwarded-* de PROXIES CONFIGURADOS (seção 23).
    // Sem configuração de proxies confiáveis, o IP usado é o da conexão direta (não spoofável).
    builder.Services.Configure<ForwardedHeadersOptions>(opcoes =>
    {
        opcoes.ForwardedHeaders = ForwardedHeaders.None;
        opcoes.KnownProxies.Clear();
        opcoes.KnownIPNetworks.Clear();
        var proxies = builder.Configuration["Rede:ProxiesConfiaveis"];
        if (!string.IsNullOrWhiteSpace(proxies))
        {
            opcoes.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in proxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IPAddress.TryParse(proxy, out var ip))
                {
                    opcoes.KnownProxies.Add(ip);
                }
            }
        }
    });

    // ---------- Apresentação (Blazor Server) ----------
    // SignalR/circuito endurecido (seções 8, 9): sem erros detalhados fora de Development
    // (não vazar exceções ao cliente); tamanho máximo de mensagem e timeouts limitados.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents(opcoes => opcoes.DetailedErrors = builder.Environment.IsDevelopment())
        .AddHubOptions(opcoes =>
        {
            opcoes.MaximumReceiveMessageSize = 64 * 1024; // 64 KB por mensagem do circuito
            opcoes.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
            opcoes.HandshakeTimeout = TimeSpan.FromSeconds(15);
            opcoes.EnableDetailedErrors = builder.Environment.IsDevelopment();
        });

    // ---------- Erros padronizados (regra 16: sem detalhes internos) ----------
    builder.Services.AddProblemDetails();

    // ---------- Health checks (Prompt 31, §13-19) ----------
    // Liveness = só "processo vivo" (§14, nunca deriva restart de dependência externa). Readiness = banco crítico
    // (§15). Dependency health = saúde de workers/integrações (§20/§16), tolerante (Degraded), sem custo por probe.
    builder.Services.AddHealthChecks()
        .AddCheck<VerificacaoVivacidade>("self", tags: ["live"])
        .AddCheck<VerificacaoBancoCatalogo>("banco-catalogo", tags: ["ready"])
        .AddCheck<VerificacaoSaudeWorkers>("workers", tags: ["dependencies"])
        // O serviço do Worker (outro processo) visto pela batida que ele grava no catálogo (OBS-003).
        .AddCheck<VerificacaoServicoWorker>("worker-servico", tags: ["dependencies"])
        .AddCheck<VerificacaoUltimaIntegracao>("integracoes", tags: ["dependencies"]);

    var app = builder.Build();

    // Implantação nova: "PortalBase.Web.exe criar-primeiro-administrador" cria o primeiro Administrador da plataforma
    // (só enquanto não existe nenhum) e sai — não sobe o site.
    if (PortalBase.Web.Hospedagem.ComandoPrimeiroAdministrador.Solicitado(args))
    {
        Environment.ExitCode = await PortalBase.Web.Hospedagem.ComandoPrimeiroAdministrador.ExecutarAsync(app.Services);
        return;
    }

    // Development: se a base de catálogo (ou as suas tabelas) tiver sido apagada, recria com a conta migradora
    // restrita ao catálogo (ConnectionStrings:CatalogoMigracao, do configurar-ambiente-local.ps1). Em Produção não roda
    // — o preflight recusa essa credencial e um catálogo apagado é incidente (restaurar backup), nunca recriado vazio.
    if (app.Environment.IsDevelopment())
    {
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await app.Services.GetRequiredService<PortalBase.Infrastructure.Persistencia.Catalogo.GarantiaCatalogoDesenvolvimento>()
            .GarantirAsync(limite.Token);
    }

    // Cria o Meter operacional (Prompt 31) no arranque para expor os gauges de saúde via OTel.
    app.Services.GetRequiredService<PortalBase.Infrastructure.Observabilidade.MetricasOperacionais>();

    // Identidade da implantação (Prompt 53, Fase 3): registra VERSION → COMMIT → BUILD → ENV no startup
    // (sem segredo). Permite correlacionar um release à imagem/commit em diagnóstico.
    var implantacaoWeb = PortalBase.Infrastructure.Producao.InformacaoImplantacao.Coletar(app.Environment.EnvironmentName);
    Log.Information(
        "Portal Base Web iniciando. versao={Versao} commit={Commit} build={Build} ambiente={Ambiente}",
        implantacaoWeb.Versao, implantacaoWeb.CommitSha, implantacaoWeb.BuildTimestamp, implantacaoWeb.Ambiente);

    // ---------- Pipeline ----------
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseForwardedHeaders();

    // Correlação de ponta a ponta (seções 11, 12): correlation id + trace id em todo request,
    // no log estruturado E no contexto de auditoria (que o sink consome). Origem = Web / ator = Usuário.
    app.Use(async (contexto, next) =>
    {
        const string Cabecalho = "X-Correlation-ID";
        var correlationId = contexto.Request.Headers[Cabecalho].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        var traceId = Activity.Current?.TraceId.ToString() ?? contexto.TraceIdentifier;
        contexto.Response.Headers[Cabecalho] = correlationId;

        var auditoria = contexto.RequestServices.GetRequiredService<IContextoAuditoriaMutavel>();
        auditoria.DefinirCorrelacao(correlationId, traceId);
        auditoria.DefinirOrigem("Web", TipoAtor.Usuario);
        auditoria.DefinirOrigemHttp(
            contexto.Connection.RemoteIpAddress?.ToString(),
            contexto.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 256)] : null);

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("TraceId", traceId))
        {
            await next(contexto);
        }
    });

    app.UseCabecalhosSeguranca();
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    // POST/PUT/DELETE: sem re-execute (a página Blazor recusaria o POST); devolve o código real com página curta.
    app.UseMiddleware<RespostaErroFormulario>();
    app.UseHttpsRedirection();
    app.UseRateLimiter();
    app.UseAuthentication();
    // Pendências da conta (MFA exigido não cadastrado, senha temporária): resolvidas antes de qualquer página.
    app.UseMiddleware<GuardaPendenciasConta>();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapEndpointsAutenticacao();
    app.MapEndpointsUpload();
    app.MapEndpointsMarca();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = registro => registro.Tags.Contains("live"),
        ResponseWriter = EscreverRespostaSaudeAsync,
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registro => registro.Tags.Contains("ready"),
        ResponseWriter = EscreverRespostaSaudeAsync,
    });

    // Dependency health (Prompt 31, §13/§20): saúde agregada de workers/integrações — não derruba readiness.
    app.MapHealthChecks("/health/dependencies", new HealthCheckOptions
    {
        Predicate = registro => registro.Tags.Contains("dependencies"),
        ResponseWriter = EscreverRespostaSaudeAsync,
    });

    app.Run();
}
catch (Exception excecao)
{
    Log.Fatal(excecao, "Falha fatal na inicialização do host.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Resposta mínima de health: apenas o estado, sem detalhes internos (regra 16).
static Task EscreverRespostaSaudeAsync(HttpContext contexto, HealthReport relatorio)
{
    contexto.Response.ContentType = "application/json";
    var carga = JsonSerializer.Serialize(new { status = relatorio.Status.ToString() });
    return contexto.Response.WriteAsync(carga);
}

/// <summary>Ponto de entrada exposto para testes de integração (WebApplicationFactory).</summary>
public partial class Program;
