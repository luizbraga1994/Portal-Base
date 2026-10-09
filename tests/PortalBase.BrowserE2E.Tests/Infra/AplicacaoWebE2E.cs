using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PortalBase.BrowserE2E.Tests.Infra;

/// <summary>
/// Prompt 54 — Fase 4 (APPLICATION HOST). Sobe o host Web REAL em <b>Kestrel</b> numa porta local
/// dinâmica, de modo que um navegador de verdade consiga acessar uma URL HTTP real. Ambiente "Testing": em
/// não-produção a conexão de banco não é obrigatória (o host boota sem MySQL); o que depender de banco falha de
/// forma segura (fail-closed), nunca vaza detalhe interno.
///
/// P62-R1: usa o modo Kestrel NATIVO do <see cref="WebApplicationFactory{T}"/> (.NET 10, <c>UseKestrel(0)</c>) —
/// UM host, UM <c>Program.Main</c>. O padrão anterior (dotnet/aspnetcore#33846: construir dois hosts — TestServer +
/// Kestrel — a partir do mesmo builder diferido) era intrinsecamente corrida: os dois hosts compartilham o MESMO
/// sinal "host iniciado" do <c>DeferredHostBuilder</c> (<c>_hostStartTcs</c>); o Kestrel o completava e o
/// <c>Start()</c> do host TestServer retornava ANTES de ele iniciar. Em máquina rápida passava; no runner do CI
/// (mais lento) o primeiro cliente encontrava o TestServer não iniciado ("The server has not been started or no web
/// application was configured") e os 20 cenários integrados falhavam na montagem (run 36932264443).
/// </summary>
public sealed class AplicacaoWebE2E : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?>? _configuracaoExtra;
    private volatile bool _iniciado;

    /// <param name="configuracaoExtra">
    /// Configuração adicional aplicada ao host (ex.: conexão MySQL real para o E2E integrado — Prompt 55).
    /// Quando <c>null</c>, o host sobe exatamente como no Prompt 54 (Testing, sem banco).
    /// </param>
    public AplicacaoWebE2E(IReadOnlyDictionary<string, string?>? configuracaoExtra = null)
    {
        _configuracaoExtra = configuracaoExtra;
        UseKestrel(0); // 127.0.0.1, porta escolhida pelo SO
    }

    /// <summary>URL base real (http://127.0.0.1:porta/) para o navegador acessar.</summary>
    public string EnderecoServidor
    {
        get
        {
            GarantirServidor();
            return ClientOptions.BaseAddress.ToString();
        }
    }

    /// <summary>
    /// Serviços do host Web REAL (o mesmo que o navegador acessa). Uso exclusivo de teste: ex. produzir um ticket de
    /// cookie EXPIRADO com a proteção real do servidor para provar a expiração de sessão (sem esperar 8 h).
    /// </summary>
    internal IServiceProvider ServicosKestrel
    {
        get
        {
            GarantirServidor();
            return Services;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");

        // P62-R1: fora de Development os Static Web Assets (blazor.web.js, CSS isolado, módulos JS) só são
        // servidos a partir do manifesto de build se habilitados explicitamente. Sem isto o host de teste
        // respondia 500 a /_framework/blazor.web.js e NENHUMA interação (@onclick) funcionava no Browser.
        builder.UseStaticWebAssets();

        if (_configuracaoExtra is not null)
        {
            // P62-R1: UseSetting (configuração de HOST), não ConfigureAppConfiguration. No hosting mínimo,
            // ConfigureAppConfiguration da fábrica só é aplicado em builder.Build() — DEPOIS de o Program.cs
            // registrar os serviços. AddInfrastructure lê ConnectionStrings:Catalogo no registro e, vazia em
            // não-produção, usa o banco de desenvolvimento: a Web do E2E integrado apontava para outro banco
            // (readiness Unhealthy, login impossível). UseSetting é visível já no registro, como as variáveis
            // de ambiente/appsettings da implantação real.
            foreach (var (chave, valor) in _configuracaoExtra)
            {
                builder.UseSetting(chave, valor);
            }
        }
    }

    // P62-R1: dois WebApplicationFactory<Program> iniciando AO MESMO TEMPO no processo de teste (coleções
    // "browser-e2e" e "e2e-integrado" rodam em paralelo) disputam estado GLOBAL — o gancho do HostFactoryResolver
    // (DiagnosticListener do processo) e o logger estático de bootstrap do Serilog (Log.Logger/CloseAndFlush no
    // Program). Reproduzido: "ObjectDisposedException: IServiceProvider" no startup de um dos hosts; no harness
    // integrado a exceção virava SKIP silencioso (os 9 integrados "skipped" do CI). Só o BOOT é serializado — os
    // testes continuam paralelos entre coleções (mesma decisão da ColecaoHostWeb do projeto de Integration).
    private static readonly SemaphoreSlim BootSerializado = new(1, 1);

    private void GarantirServidor()
    {
        if (_iniciado)
        {
            return;
        }

        BootSerializado.Wait();
        try
        {
            if (!_iniciado)
            {
                StartServer(); // constrói e INICIA o único host (Kestrel) e publica o endereço real em ClientOptions
                _iniciado = true;
            }
        }
        finally
        {
            BootSerializado.Release();
        }
    }
}
