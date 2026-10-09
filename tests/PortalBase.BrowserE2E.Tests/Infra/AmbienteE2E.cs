using System.Globalization;
using Microsoft.Playwright;

namespace PortalBase.BrowserE2E.Tests.Infra;

/// <summary>
/// Coleção que compartilha um ÚNICO host Web (Kestrel) + um ÚNICO navegador entre os testes E2E
/// (Fase 38/39: isolamento por contexto de navegador, execução serial dentro da coleção — evita
/// corridas de porta/circuito). Cada teste abre seu próprio <see cref="IBrowserContext"/> limpo.
/// </summary>
[CollectionDefinition(Nome)]
public sealed class ColecaoE2E : ICollectionFixture<AmbienteE2E>
{
    public const string Nome = "browser-e2e";
}

/// <summary>
/// Ambiente de Browser E2E (Prompt 54). Sobe o host real (Kestrel), inicializa o Playwright e lança
/// o Chromium pré-instalado. Se o navegador NÃO puder ser lançado (infra ausente), os testes de
/// navegador são <b>SKIPPED/BLOCKED</b> com o motivo real — nunca declarados PASS. Os fluxos autenticados
/// (que exigem tenant/usuário semeados em MySQL) vivem no ambiente INTEGRADO (P62-R1:
/// <see cref="AmbienteE2EIntegrado"/> / <c>FluxosAutenticadosIntegradoE2ETests</c>).
/// </summary>
public sealed class AmbienteE2E : IAsyncLifetime, IDisposable
{
    private AplicacaoWebE2E? _app;
    private IPlaywright? _playwright;
    private bool _descartado;

    /// <summary>URL base real do host (sem barra final).</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    public IBrowser? Navegador { get; private set; }

    /// <summary>Verdadeiro só quando o Chromium foi realmente lançado.</summary>
    public bool NavegadorDisponivel { get; private set; }

    /// <summary>Motivo real da indisponibilidade do navegador (para o Skip/relatório).</summary>
    public string? MotivoBloqueioNavegador { get; private set; }

    /// <summary>Descrição do navegador (browser+versão) para o relatório (Fase 49).</summary>
    public string InfoNavegador { get; private set; } = "n/d";

    /// <summary>Diretório de evidências (screenshots/traces) — gitignored (artifacts/).</summary>
    public string DiretorioEvidencias { get; }

    public AmbienteE2E()
    {
        DiretorioEvidencias = Path.Combine(LocalizarRaizRepo(), "artifacts", "e2e");
        Directory.CreateDirectory(DiretorioEvidencias);
    }

    public async Task InitializeAsync()
    {
        // O host Web sobe em Testing (não exige banco) — uma falha aqui é regressão REAL e deve aparecer.
        _app = new AplicacaoWebE2E();
        BaseUrl = _app.EnderecoServidor.TrimEnd('/');

        // O lançamento do navegador é a parte dependente de infra: se falhar, classifica BLOCKED.
        try
        {
            _playwright = await Playwright.CreateAsync();
            Navegador = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                // Sandbox do Chromium não funciona dentro de contêineres não privilegiados.
                Args = ["--no-sandbox", "--disable-dev-shm-usage"],
                // Usa o Chromium pré-instalado no ambiente quando presente (evita qualquer download).
                ExecutablePath = LocalizarChromium(),
            });
            InfoNavegador = "Chromium " + Navegador.Version;
            NavegadorDisponivel = true;
        }
        catch (Exception excecao)
        {
            NavegadorDisponivel = false;
            MotivoBloqueioNavegador = excecao.GetType().Name + ": " + PrimeiraLinha(excecao.Message);
        }
    }

    /// <summary>
    /// Executa um teste de navegador num contexto limpo, com <b>trace + screenshot em falha</b>
    /// (Fase 35/36). Em sucesso, o trace é descartado (não versiona dezenas de artefatos). O trace
    /// não inclui "sources" (evita anexar caminhos/código) e nenhuma página navegada carrega segredo.
    /// </summary>
    public async Task ExecutarNavegadorAsync(
        string nomeCenario,
        Func<IPage, Task> corpo,
        int largura = 1280,
        int altura = 800)
    {
        ArgumentNullException.ThrowIfNull(corpo);
        Skip.IfNot(NavegadorDisponivel,
            "BLOCKED: navegador indisponível no ambiente — " + (MotivoBloqueioNavegador ?? "motivo desconhecido"));

        var navegador = Navegador!;
        await using var contexto = await navegador.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = largura, Height = altura },
            IgnoreHTTPSErrors = false,
            Locale = "pt-BR",
        });
        await contexto.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = false,
        });

        var pagina = await contexto.NewPageAsync();
        try
        {
            await corpo(pagina).ConfigureAwait(false);
            await contexto.Tracing.StopAsync(new TracingStopOptions()); // descarta em sucesso
        }
        catch
        {
            var carimbo = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var baseNome = SanitizarNome(nomeCenario) + "-" + carimbo;
            try
            {
                await pagina.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(DiretorioEvidencias, baseNome + ".png"),
                    FullPage = true,
                });
                await contexto.Tracing.StopAsync(new TracingStopOptions
                {
                    Path = Path.Combine(DiretorioEvidencias, baseNome + ".trace.zip"),
                });
            }
            catch
            {
                // Nunca deixar a captura de evidência mascarar a falha original do teste.
            }

            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (Navegador is not null)
        {
            await Navegador.DisposeAsync();
        }

        Dispose();
    }

    public void Dispose()
    {
        if (_descartado)
        {
            return;
        }

        _descartado = true;
        _playwright?.Dispose();
        _app?.Dispose();
    }

    /// <summary>
    /// Caminho do Chromium pré-instalado, quando existir (variável explícita ou o symlink padrão do
    /// ambiente Playwright). Retorna <c>null</c> para deixar o driver resolver por conta própria.
    /// </summary>
    private static string? LocalizarChromium()
    {
        var candidatos = new[]
        {
            Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE"),
            "/opt/pw-browsers/chromium",
            "/opt/pw-browsers/chromium-1194/chrome-linux/chrome",
        };

        foreach (var candidato in candidatos)
        {
            if (!string.IsNullOrWhiteSpace(candidato) && File.Exists(candidato))
            {
                return candidato;
            }
        }

        return null;
    }

    private static string PrimeiraLinha(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return "sem mensagem";
        }

        var quebra = texto.IndexOf('\n', StringComparison.Ordinal);
        var linha = quebra >= 0 ? texto[..quebra] : texto;
        return linha.Length > 200 ? linha[..200] : linha;
    }

    private static string SanitizarNome(string nome)
    {
        var limpos = nome.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(limpos);
    }

    private static string LocalizarRaizRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortalBase.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
