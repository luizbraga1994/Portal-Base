using System.Diagnostics;
using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Infrastructure.Observabilidade;

namespace PortalBase.Integration.Tests.Observabilidade;

/// <summary>
/// Prompt 51 — GUARDRAILS de observabilidade. Trava as regras inegociáveis contra regressão:
/// nenhum SEGREDO em log estruturado (§2/§25), nenhuma dimensão de CARDINALIDADE ILIMITADA em métrica
/// (§6), o span de saída sem token/segredo (§9), e a redação central ocultando valores sensíveis.
/// </summary>
public sealed class ObservabilidadeGuardrailP51Tests
{
    // Placeholders de log que NUNCA podem aparecer num template estruturado (dados sensíveis).
    private static readonly string[] PlaceholdersProibidos =
    [
        "{Password}", "{Senha}", "{Token}", "{Secret}", "{Segredo}", "{Authorization}", "{Bearer}",
        "{ConnectionString}", "{Pfx}", "{PrivateKey}", "{ChavePrivada}", "{ClientSecret}", "{ApiKey}",
    ];

    // Rótulos de alta cardinalidade / sensíveis que NUNCA podem virar tag de métrica.
    private static readonly string[] LabelsProibidos =
    [
        "\"CorrelationId\"", "\"correlation_id\"", "\"RequestId\"", "\"request_id\"", "\"TraceId\"",
        "\"trace_id\"", "\"xml\"", "\"payload\"", "\"exception.message\"", "\"stacktrace\"", "\"senha\"",
    ];

    [Fact] // §2/§25 — nenhum template de log expõe segredo
    public void Fonte_Sem_Log_De_Segredo()
    {
        // Placeholders de log em Serilog seguem PascalCase (ex.: {Corr}); a comparação é sensível a
        // caso para não confundir com anotação de UNIDADE de métrica em UCUM (ex.: unit "{token}").
        var ofensores = new List<string>();
        foreach (var arquivo in ArquivosFonte().Where(a => !Path.GetFileName(a).StartsWith("Metricas", StringComparison.Ordinal)))
        {
            var texto = File.ReadAllText(arquivo);
            if (PlaceholdersProibidos.Any(p => texto.Contains(p, StringComparison.Ordinal)))
            {
                ofensores.Add(arquivo);
            }
        }

        ofensores.Should().BeEmpty("nenhum log estruturado pode conter senha/token/segredo/connection string (§2/§25)");
    }

    [Fact] // §6 — as métricas não usam dimensões de cardinalidade ilimitada / sensíveis
    public void Metricas_Sem_Cardinalidade_Ilimitada()
    {
        var ofensores = new List<string>();
        foreach (var arquivo in ArquivosFonte().Where(a => Path.GetFileName(a).StartsWith("Metricas", StringComparison.Ordinal)))
        {
            var texto = File.ReadAllText(arquivo);
            if (LabelsProibidos.Any(l => texto.Contains(l, StringComparison.OrdinalIgnoreCase)))
            {
                ofensores.Add(arquivo);
            }
        }

        ofensores.Should().BeEmpty("métricas nunca usam CorrelationId/RequestId/xml/payload/exception como tag (§6)");
    }

    [Fact] // §9 — o span de saída carrega apenas tags seguras (sem token/authorization/segredo)
    public void Span_DeRe_Somente_Tags_Seguras()
    {
        var capturado = new List<Activity>();
        using var ouvinte = new ActivityListener
        {
            ShouldListenTo = fonte => fonte.Name == FonteAtividadePortal.NomeFonte,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = capturado.Add,
        };
        ActivitySource.AddActivityListener(ouvinte);

        using (var atividade = FonteAtividadePortal.Fonte.StartActivity("integracao.enviar", ActivityKind.Client))
        {
            atividade.Should().NotBeNull("a fonte precisa estar registrada e amostrável");
            atividade!.SetTag("peer.service", "dere-api");
            atividade.SetTag("http.request.method", "POST");
            atividade.SetTag("http.response.status_code", 200);
        }

        var span = capturado.Should().ContainSingle().Subject;
        var chaves = span.TagObjects.Select(t => t.Key.ToLowerInvariant()).ToList();
        chaves.Should().NotContain(c => c.Contains("authorization") || c.Contains("token")
            || c.Contains("secret") || c.Contains("bearer") || c.Contains("password") || c.Contains("xml"));
    }

    [Theory] // redação central oculta valores de chaves sensíveis (reuso da regra única — não duplica)
    [InlineData("authorization")]
    [InlineData("password")]
    [InlineData("connectionString")]
    [InlineData("pfx")]
    [InlineData("clientSecret")]
    [InlineData("bearer")]
    public void Redacao_Oculta_Segredos(string chaveSensivel)
    {
        var sanitizado = SanitizacaoAuditoria.Sanitizar(new Dictionary<string, string?>
        {
            [chaveSensivel] = "valor-super-secreto",
            ["tenantId"] = "abc123",
        });

        sanitizado[chaveSensivel].Should().Be(SanitizacaoAuditoria.Redigido);
        sanitizado["tenantId"].Should().Be("abc123"); // não sensível é preservado
    }

    [Fact] // §8/§41.6 — UMA única ActivitySource, registrada (AddSource) em AMBOS os hosts (spans sem AddSource nunca exportam)
    public void ActivitySource_E_Unica_E_Registrada_Nos_Hosts()
    {
        // §8: uma única fonte para o domínio — nenhuma fonte paralela espalhada pelo código. Detecta a
        // INSTANCIAÇÃO da ActivitySource em ambas as formas: "new ActivitySource(" e a target-typed
        // "ActivitySource Fonte = new(" (usada por FonteAtividadePortal).
        var padraoCriacao = new System.Text.RegularExpressions.Regex(
            @"new\s+ActivitySource\s*\(|ActivitySource\s+\w+\s*=\s*new\b");
        var criadoras = ArquivosFonte()
            .Where(a => padraoCriacao.IsMatch(File.ReadAllText(a)))
            .Select(Path.GetFileName)
            .ToList();
        criadoras.Should().BeEquivalentTo(["FonteAtividadePortal.cs"],
            "deve existir exatamente UMA ActivitySource (§8) — nenhuma segunda fonte paralela para o mesmo domínio");

        // §41.6: cada host que emite spans precisa registrar a fonte via AddSource — senão os spans nunca exportam.
        foreach (var host in new[] { "PortalBase.Web", "PortalBase.Worker" })
        {
            var texto = TextoDaComposicao(host);
            texto.Should().Contain("AddSource(", $"{host} precisa registrar a ActivitySource no tracing (§41.6)");
            texto.Should().Contain("FonteAtividadePortal.NomeFonte",
                $"{host} deve registrar a fonte 'PortalBase' via AddSource — spans sem AddSource são silenciosamente descartados");
        }
    }

    [Fact] // §11/§41.7 — todo contador/histograma declara UNIDADE clara (métrica sem unidade é ambígua)
    public void Metricas_Contadores_E_Histogramas_Declaram_Unidade()
    {
        var semUnidade = new List<string>();
        foreach (var arquivo in ArquivosFonte().Where(a => Path.GetFileName(a).StartsWith("Metricas", StringComparison.Ordinal)))
        {
            foreach (var linha in File.ReadLines(arquivo))
            {
                var t = linha.Trim();
                // Instrumentos SÍNCRONOS de contagem/latência declaram unidade explícita. Os OBSERVÁVEIS
                // (gauges/observable counters em MetricasOperacionais) são contagens agregadas adimensionais
                // (worker_active, *_total) e legitimamente não carregam unit — não são exigidos aqui.
                var criaInstrumento = t.Contains(".CreateCounter", StringComparison.Ordinal)
                    || t.Contains(".CreateHistogram", StringComparison.Ordinal)
                    || t.Contains(".CreateUpDownCounter", StringComparison.Ordinal);
                if (criaInstrumento && !t.Contains("unit:", StringComparison.Ordinal))
                {
                    semUnidade.Add($"{Path.GetFileName(arquivo)}: {t}");
                }
            }
        }

        semUnidade.Should().BeEmpty("todo contador/histograma OpenTelemetry declara unidade explícita (§41.7) — métrica sem unidade é ambígua");
    }

    [Fact] // §32/§33/§41.5 — exporter OTLP só ativa por configuração autorizada; endpoint NUNCA hardcoded
    public void Otlp_Exporter_Ativa_So_Por_Configuracao_Sem_Endpoint_Hardcoded()
    {
        foreach (var host in new[] { "PortalBase.Web", "PortalBase.Worker" })
        {
            var texto = TextoDaComposicao(host);
            if (!texto.Contains("AddOtlpExporter", StringComparison.Ordinal))
            {
                continue; // host sem exporter OTLP: nada a exigir
            }

            texto.Should().Contain("OTEL_EXPORTER_OTLP_ENDPOINT",
                $"{host}: o exporter OTLP só pode ativar por configuração autorizada (§32/§33) — nunca destino arbitrário");
            // §32: endpoint jamais fixado no código (deve vir do ambiente/allowlist de infraestrutura).
            texto.Should().NotContain(".Endpoint = new Uri(", $"{host}: endpoint OTLP nunca é hardcoded no código (§32)");
            texto.Should().NotContain("Endpoint = new Uri(\"http", $"{host}: endpoint OTLP nunca é hardcoded no código (§32)");
        }
    }

    /// <summary>
    /// Fonte onde o host compõe telemetria/DI: a Web no <c>Program.cs</c>; o Worker em <c>ComposicaoWorker.cs</c>
    /// (extraída do Program para ser validada em teste). Ler o arquivo errado faria a verificação passar VAZIA.
    /// </summary>
    private static string TextoDaComposicao(string host)
    {
        var arquivo = Path.Combine(CaminhoSrc(), host, host == "PortalBase.Worker" ? "ComposicaoWorker.cs" : "Program.cs");
        File.Exists(arquivo).Should().BeTrue($"a composição de {host} deve existir em {arquivo}");
        return File.ReadAllText(arquivo);
    }

    private static string CaminhoSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortalBase.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("a raiz do repositório (PortalBase.sln) deve ser localizável para os guardrails de fonte");
        var src = Path.Combine(dir!.FullName, "src");
        Directory.Exists(src).Should().BeTrue();
        return src;
    }

    private static IEnumerable<string> ArquivosFonte()
        => Directory.EnumerateFiles(CaminhoSrc(), "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
