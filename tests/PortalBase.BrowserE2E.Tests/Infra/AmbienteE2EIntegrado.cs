using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using MySql.Data.MySqlClient;
using PortalBase.Application.DependencyInjection;
using PortalBase.Infrastructure.DependencyInjection;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Worker;

namespace PortalBase.BrowserE2E.Tests.Infra;

/// <summary>
/// Prompt 55 — ambiente E2E INTEGRADO: liga as camadas REAIS num organismo único —
/// <b>Browser → Web (Kestrel) → MySQL real → Worker real (TrabalhadorHeartbeat hospedado) → MySQL →
/// read-model → Browser</b>.
///
/// Requer <c>PORTALBASE_TEST_MYSQL_ADMIN</c> (conexão administrativa: CREATE DATABASE/USER/GRANT).
/// Sem ela — como no ambiente de build sem Docker/MySQL — <see cref="Disponivel"/> é falso e os
/// testes integrados são classificados <b>BLOCKED</b> (nunca simulados como PASS).
///
/// P62-R1: com a variável PRESENTE (o CI provê o MySQL), uma falha de montagem NÃO vira skip silencioso —
/// <see cref="Exigir"/> FALHA o teste com a causa sanitizada. Antes, qualquer exceção no setup marcava os
/// cenários como BLOCKED e o workflow reportava "skipped" sem que a causa real aparecesse.
/// </summary>
public sealed partial class AmbienteE2EIntegrado : IAsyncLifetime, IDisposable
{
    private AplicacaoWebE2E? _web;
    private IHost? _worker;
    private IPlaywright? _playwright;
    private ServiceProvider? _provedorSuporte;
    private readonly List<Guid> _tenantsProvisionados = [];
    private Dictionary<string, string?> _configuracao = new(StringComparer.Ordinal);
    private string _catalogoDb = string.Empty;
    private string _appUser = string.Empty;
    private bool _descartado;

    /// <summary>Verdadeiro só quando MySQL real + Web + Worker + navegador subiram de fato.</summary>
    public bool Disponivel { get; private set; }

    /// <summary>Motivo real da indisponibilidade (para o Skip/relatório) — sem segredo.</summary>
    public string? Motivo { get; private set; }

    /// <summary>Verdadeiro quando o ambiente FOI configurado (<c>PORTALBASE_TEST_MYSQL_ADMIN</c> presente).</summary>
    public bool Configurado { get; private set; }

    public string BaseUrl { get; private set; } = string.Empty;

    public IBrowser? Navegador { get; private set; }

    public async Task InitializeAsync()
    {
        var conexaoAdmin = Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN");
        if (string.IsNullOrWhiteSpace(conexaoAdmin))
        {
            Disponivel = false;
            Motivo = "MySQL indisponível — PORTALBASE_TEST_MYSQL_ADMIN ausente (sem Docker/MySQL no ambiente de build).";
            return;
        }

        Configurado = true;

        try
        {
            var configuracao = await PrepararBancoAsync(conexaoAdmin).ConfigureAwait(false);
            _configuracao = configuracao;

            // Provedor de SUPORTE (DI de produção sobre o MESMO MySQL) — usado pelo seed do cenário e pelas
            // asserções de read-model/auditoria (nunca acessado pelo Browser; é infraestrutura de teste).
            _provedorSuporte = ConstruirProvedorSuporte(configuracao);

            // Web REAL em Kestrel, apontando para o MySQL real (mesma configuração do Worker).
            _web = new AplicacaoWebE2E(configuracao);
            BaseUrl = _web.EnderecoServidor.TrimEnd('/');

            // Worker REAL hospedado (a mesma classe BackgroundService que o processo Worker executa).
            _worker = ConstruirWorker(configuracao);
            await _worker.StartAsync().ConfigureAwait(false);

            // Navegador real (Chromium pré-instalado).
            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            Navegador = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-dev-shm-usage"],
                ExecutablePath = LocalizarChromium(),
            }).ConfigureAwait(false);

            Disponivel = true;
        }
        catch (Exception excecao)
        {
            Disponivel = false;
            Motivo = "Falha ao montar o ambiente integrado: " + DescreverSanitizado(excecao);
        }
    }

    /// <summary>
    /// Pré-condição dos cenários integrados. Sem configuração → BLOCKED (skip explicado). COM configuração e
    /// falha de montagem → FALHA (nunca skip silencioso), com a causa sanitizada (sem senha/connection string).
    /// </summary>
    public void Exigir()
    {
        Skip.IfNot(Configurado, "BLOCKED (infra): " + (Motivo ?? "ambiente integrado não configurado."));
        if (!Disponivel)
        {
            throw new Xunit.Sdk.XunitException(
                "Ambiente integrado CONFIGURADO (PORTALBASE_TEST_MYSQL_ADMIN) mas indisponível — " + (Motivo ?? "causa desconhecida."));
        }
    }

    /// <summary>Pasta onde a Web grava os e-mails enviados (Email:Modo=PastaLocal) neste ambiente.</summary>
    public string PastaEmails { get; } = Path.Combine(Path.GetTempPath(), "portalbase-e2e-emails-" + Guid.NewGuid().ToString("N"));

    /// <summary>Pasta do store de backups deste run (artefatos cifrados; removida no fim).</summary>
    public string PastaBackups { get; } = Path.Combine(Path.GetTempPath(), "portalbase-e2e-backups-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Verdadeiro quando o host tem <c>mysqldump</c> e <c>mysql</c> no PATH: o executor FÍSICO de backup é ligado e os
    /// cenários de backup/restauração rodam de verdade. Sem as ferramentas ⇒ esses cenários ficam BLOCKED.
    /// </summary>
    public bool BackupFisicoDisponivel { get; } = BinarioNoPath("mysqldump") && BinarioNoPath("mysql");

    /// <summary>Serviços do host Web REAL (Kestrel) — só para asserções/infra de teste.</summary>
    internal IServiceProvider ServicosWeb
        => (_web ?? throw new InvalidOperationException("Ambiente integrado indisponível.")).ServicosKestrel;

    // Causa da falha de montagem para o relatório: tipo + 1ª linha da mensagem (inclusive das internas), com
    // qualquer credencial mascarada. Nunca a connection string inteira, nunca stack trace.
    private static string DescreverSanitizado(Exception excecao)
    {
        var partes = new List<string>();
        for (var atual = excecao; atual is not null && partes.Count < 3; atual = atual.InnerException)
        {
            var linha = atual.Message.Split('\n', 2)[0].Trim();
            partes.Add(atual.GetType().Name + ": " + MascararCredenciais(linha));
        }

        return string.Join(" <- ", partes);
    }

    private static string MascararCredenciais(string texto) => RegexCredencial().Replace(texto, "$1=***");

    [GeneratedRegex(@"(?i)\b(pwd|password|senha|secret|token)\s*=\s*[^;'""\s]*")]
    private static partial Regex RegexCredencial();

    private async Task<Dictionary<string, string?>> PrepararBancoAsync(string conexaoAdmin)
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        _catalogoDb = $"pb_e2e_cat_{sufixo}";
        _appUser = $"pb_e2e_app_{sufixo}";
        var appSenha = $"Ap1_{sufixo}";

        var admin = new MySqlConnectionStringBuilder(conexaoAdmin);

        await using (var conexao = new MySqlConnection(conexaoAdmin))
        {
            await conexao.OpenAsync().ConfigureAwait(false);
            await ExecutarAsync(conexao, $"CREATE DATABASE `{_catalogoDb}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs").ConfigureAwait(false);
            await ExecutarAsync(conexao, $"CREATE USER '{_appUser}'@'%' IDENTIFIED BY '{appSenha}'").ConfigureAwait(false);
        }

        var conexaoCatalogo = new MySqlConnectionStringBuilder(conexaoAdmin) { Database = _catalogoDb }.ConnectionString;

        var configuracao = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Catalogo"] = conexaoCatalogo,
            ["BancoTenant:Servidor"] = admin.Server,
            ["BancoTenant:Porta"] = admin.Port.ToString(CultureInfo.InvariantCulture),
            ["BancoTenant:SslMode"] = admin.SslMode.ToString(),
            ["BancoTenant:PermitirRecuperacaoChavePublica"] = admin.AllowPublicKeyRetrieval ? "true" : "false",
            ["BancoTenant:AdminUsuario"] = admin.UserID,
            ["BancoTenant:AdminSenhaRef"] = "adm",
            ["BancoTenant:RuntimeUsuario"] = _appUser,
            ["BancoTenant:RuntimeSenhaRef"] = "app",
            ["Segredos:adm"] = admin.Password,
            ["Segredos:app"] = appSenha,
            ["SegurancaIdentidade:ChaveProtecaoMfaRef"] = "mfa",
            ["SegurancaIdentidade:ChaveHashRef"] = "hashid",
            ["Segredos:mfa"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Segredos:hashid"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Auditoria:ChaveIntegridadeRef"] = "auditint",
            ["Segredos:auditint"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            // P62-R1: a suíte integrada faz ~25 logins + ~25 step-ups MFA REAIS a partir de 127.0.0.1 em poucos minutos;
            // com os padrões (10 por janela de 5 min, por IP) o limitador cortaria — corretamente — os logins
            // excedentes antes do handler. O limitador CONTINUA ATIVO: só o teto é configurado, como a implantação
            // permite (Seguranca:RateLimit). A imposição com teto baixo é provada por RateLimitWebTests (Integration).
            ["Seguranca:RateLimit:LoginPorJanela"] = "500",
            ["Seguranca:RateLimit:MfaPorJanela"] = "500",
            ["Seguranca:RateLimit:UploadPorJanela"] = "500",
            ["Seguranca:RateLimit:RecuperacaoSenhaPorJanela"] = "500",

            // Sem antivírus integrado no ambiente de teste: a planilha é aceita SEM varredura (mesma decisão de risco
            // formal documentada para ambientes sem antivírus — Upload:AntivirusObrigatorio=false). Infectado/Erro nunca.
            ["Upload:AntivirusObrigatorio"] = "false",

            // Sessão conferida no catálogo a CADA requisição (padrão: a cada 60 s): os testes provam a revogação na hora.
            ["Seguranca:Sessao:RevalidacaoSegundos"] = "0",

            // E-mail de disparo: cada e-mail vira um .eml nesta pasta (o teste do "Esqueci a senha" lê o link de lá).
            ["Email:Modo"] = "PastaLocal",
            ["Email:UrlPublicaPortal"] = "http://portal.e2e.local",
            ["Email:Remetente:Email"] = "nao-responda@portal.e2e.local",
            ["Email:PastaLocal:Caminho"] = PastaEmails,

            // ARQ-006: o trabalho pesado roda em TAREFA EM SEGUNDO PLANO, executada pela própria Web
            // (no ambiente Testing o executor só liga quando pedido). Busca curta para o E2E não esperar à toa.
            ["Tarefas:ExecutorHabilitado"] = "true",
            ["Tarefas:IntervaloBuscaSegundos"] = "1",
        };

        if (BackupFisicoDisponivel)
        {
            // Backup/restauração FÍSICOS pela tela (mysqldump/mysql reais, artefato cifrado com chave do "cofre" de teste).
            configuracao["Backup:ExecutorMySql:Habilitado"] = "true";
            configuracao["Backup:ChaveCriptografiaRef"] = "bkp";
            configuracao["Segredos:bkp"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            configuracao["ArmazenamentoBackup:DiretorioRaiz"] = PastaBackups;
        }

        // Migra o catálogo (schema real) antes de subir Web/Worker.
        var servicos = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(configuracao).Build();
        servicos.AddSingleton<IConfiguration>(config);
        servicos.AddLogging();
        servicos.AddApplication();
        servicos.AddInfrastructure(config, conexaoObrigatoria: true);
        await using var provedor = servicos.BuildServiceProvider();
        await using (var escopo = provedor.CreateAsyncScope())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            await catalogo.Database.MigrateAsync().ConfigureAwait(false);
        }

        return configuracao;
    }

    private static IHost ConstruirWorker(IReadOnlyDictionary<string, string?> configuracao)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(configuracao);
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration, conexaoObrigatoria: true);
        // O Worker REAL (batida de saúde gravada no catálogo) — BackgroundService, não handler direto. Acrescente aqui os
        // trabalhadores do seu domínio que o E2E precise observar.
        builder.Services.AddHostedService<TrabalhadorHeartbeat>();
        return builder.Build();
    }

    // Provedor de SUPORTE ao teste: MESMA DI de produção (AddApplication + AddInfrastructure) sobre o MESMO MySQL
    // real. Serve o SEED do cenário (provisiona tenant, cria usuário/MFA/papéis, semeia o arquivo) e as
    // asserções de read-model/auditoria — nunca a cadeia principal (o Browser fala só com o Web/Kestrel).
    private static ServiceProvider ConstruirProvedorSuporte(IReadOnlyDictionary<string, string?> configuracao)
    {
        var servicos = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(configuracao).Build();
        servicos.AddSingleton<IConfiguration>(config);
        servicos.AddLogging();
        servicos.AddApplication();
        servicos.AddInfrastructure(config, conexaoObrigatoria: true);
        return servicos.BuildServiceProvider();
    }

    /// <summary>Escopo de DI de suporte (seed/asserções) — NUNCA usado pelo Browser.</summary>
    internal IServiceScope CriarEscopoSuporte()
        => (_provedorSuporte ?? throw new InvalidOperationException("Ambiente integrado indisponível.")).CreateScope();

    /// <summary>Registra um tenant provisionado neste run para expurgo da base física no fim (tenant_{id:N}).</summary>
    internal void RegistrarTenant(Guid tenantId) => _tenantsProvisionados.Add(tenantId);

    public async Task DisposeAsync()
    {
        if (Navegador is not null)
        {
            await Navegador.DisposeAsync().ConfigureAwait(false);
        }

        if (_worker is not null)
        {
            await _worker.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _worker.Dispose();
        }

        if (_web is not null)
        {
            await _web.DisposeAsync().ConfigureAwait(false);
        }

        if (_provedorSuporte is not null)
        {
            await _provedorSuporte.DisposeAsync().ConfigureAwait(false);
        }

        await LimparBancoAsync().ConfigureAwait(false);
        if (Directory.Exists(PastaEmails))
        {
            Directory.Delete(PastaEmails, recursive: true);
        }

        if (Directory.Exists(PastaBackups))
        {
            Directory.Delete(PastaBackups, recursive: true);
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
    }

    private async Task<IReadOnlyList<string>> NomesBasesDoCatalogoAsync(MySqlConnection conexao)
    {
        var nomes = new List<string>();
        try
        {
            await using var comando = conexao.CreateCommand();
#pragma warning disable CA2100 // Nome do catálogo de teste gerado pelo próprio ambiente (pb_e2e_cat_<hex>).
            comando.CommandText = $"SELECT `NomeBaseFisica` FROM `{_catalogoDb}`.`tenant`";
#pragma warning restore CA2100
            await using var leitor = await comando.ExecuteReaderAsync().ConfigureAwait(false);
            while (await leitor.ReadAsync().ConfigureAwait(false))
            {
                var nome = leitor.GetString(0);
                if (System.Text.RegularExpressions.Regex.IsMatch(nome, "^[a-z][a-z0-9_]{1,63}$"))
                {
                    nomes.Add(nome);
                }
            }
        }
        catch (MySqlException)
        {
            // catálogo sem a tabela: nada a remover
        }

        return nomes;
    }

    private async Task LimparBancoAsync()
    {
        var conexaoAdmin = Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN");
        if (string.IsNullOrWhiteSpace(conexaoAdmin) || string.IsNullOrEmpty(_catalogoDb))
        {
            return;
        }

        try
        {
            await using var conexao = new MySqlConnection(conexaoAdmin);
            await conexao.OpenAsync().ConfigureAwait(false);

            // Bases dos tenants deste run pelo nome físico gravado no catálogo dedicado (nome escolhido do tenant).
            foreach (var nomeBase in await NomesBasesDoCatalogoAsync(conexao).ConfigureAwait(false))
            {
                // Bases derivadas da restauração pela tela (preservada / preparo / teste): {base}_ant…, {base}_rst…, {base}_tst….
                foreach (var derivada in await BasesDerivadasAsync(conexao, nomeBase).ConfigureAwait(false))
                {
                    await ExecutarSeguroAsync(conexao, $"DROP DATABASE IF EXISTS `{derivada}`").ConfigureAwait(false);
                }

                await ExecutarSeguroAsync(conexao, $"DROP DATABASE IF EXISTS `{nomeBase}`").ConfigureAwait(false);
            }

            foreach (var tenantId in _tenantsProvisionados)
            {
                await ExecutarSeguroAsync(conexao, $"DROP DATABASE IF EXISTS `tenant_{tenantId:N}`").ConfigureAwait(false);
            }

            await ExecutarSeguroAsync(conexao, $"DROP DATABASE IF EXISTS `{_catalogoDb}`").ConfigureAwait(false);
            await ExecutarSeguroAsync(conexao, $"DROP USER IF EXISTS '{_appUser}'@'%'").ConfigureAwait(false);
        }
        catch (MySqlException)
        {
            // limpeza best-effort — nunca mascara o resultado do teste.
        }
    }

    private static async Task<IReadOnlyList<string>> BasesDerivadasAsync(MySqlConnection conexao, string nomeBase)
    {
        var nomes = new List<string>();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME LIKE @ant OR SCHEMA_NAME LIKE @rst OR SCHEMA_NAME LIKE @tst";
        comando.Parameters.AddWithValue("@ant", nomeBase.Replace("_", "\\_", StringComparison.Ordinal) + "\\_ant%");
        comando.Parameters.AddWithValue("@rst", nomeBase.Replace("_", "\\_", StringComparison.Ordinal) + "\\_rst%");
        comando.Parameters.AddWithValue("@tst", nomeBase.Replace("_", "\\_", StringComparison.Ordinal) + "\\_tst%");
        await using var leitor = await comando.ExecuteReaderAsync().ConfigureAwait(false);
        while (await leitor.ReadAsync().ConfigureAwait(false))
        {
            var nome = leitor.GetString(0);
            if (Regex.IsMatch(nome, "^[a-z][a-z0-9_]{1,63}$"))
            {
                nomes.Add(nome);
            }
        }

        return nomes;
    }

    private static bool BinarioNoPath(string nome)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(pasta => File.Exists(Path.Combine(pasta, nome)) || File.Exists(Path.Combine(pasta, nome + ".exe")));

    private static async Task ExecutarAsync(MySqlConnection conexao, string sql)
    {
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task ExecutarSeguroAsync(MySqlConnection conexao, string sql)
    {
        try
        {
            await ExecutarAsync(conexao, sql).ConfigureAwait(false);
        }
        catch (MySqlException)
        {
            // best-effort
        }
    }

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
}

/// <summary>Coleção do E2E integrado (host + worker + navegador compartilhados; execução serial).</summary>
[CollectionDefinition(Nome)]
public sealed class ColecaoE2EIntegrado : ICollectionFixture<AmbienteE2EIntegrado>
{
    public const string Nome = "e2e-integrado";
}
