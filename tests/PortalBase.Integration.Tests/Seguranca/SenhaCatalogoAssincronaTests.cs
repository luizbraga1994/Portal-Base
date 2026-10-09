using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.DependencyInjection;
using PortalBase.Infrastructure.DependencyInjection;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Infrastructure.Segredos;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// INT-002 (auditoria de 08/10/2026) — a senha do catálogo era lida do cofre de forma SÍNCRONA (sync-over-async) a cada
/// DbContext criado, e o cache não tinha "single-flight": com o cofre lento (Key Vault), cada tela aberta bloqueava uma
/// thread esperando o cofre, e N leituras simultâneas viravam N chamadas ao cofre; com o cofre fora, tudo parava mesmo
/// com a senha lida minutos antes. Agora: uma busca por vez por segredo, valor anterior servido (por tempo limitado)
/// quando o cofre falha, e a senha do catálogo resolvida de forma ASSÍNCRONA só quando a conexão abre.
/// </summary>
public sealed class SenhaCatalogoAssincronaTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Leituras_Simultaneas_Do_Mesmo_Segredo_Fazem_Uma_Unica_Busca_No_Cofre()
    {
        var cofre = new CofreLento { Valor = "s3nh4", Atraso = TimeSpan.FromMilliseconds(200) };
        var cache = new ProvedorSegredosCache(cofre, new RelogioMutavel(Inicio), TimeSpan.FromMinutes(5));

        var leituras = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.ObterAsync("catalogo").AsTask()));

        leituras.Should().OnlyContain(v => v == "s3nh4");
        cofre.Chamadas.Should().Be(1, "as outras 19 leituras esperam a primeira busca em vez de ir ao cofre");
    }

    [Fact]
    public async Task Cofre_Fora_Serve_O_Valor_Anterior_Por_Tempo_Limitado_Sem_Insistir_A_Cada_Leitura()
    {
        var cofre = new CofreLento { Valor = "v1" };
        var relogio = new RelogioMutavel(Inicio);
        var cache = new ProvedorSegredosCache(cofre, relogio, TimeSpan.FromMinutes(5));
        (await cache.ObterAsync("catalogo")).Should().Be("v1");

        // TTL vencido e cofre fora: continua com a senha lida antes (o portal não para porque o cofre piscou).
        cofre.Falha = new TimeoutException("cofre indisponível");
        relogio.AgoraUtc = Inicio.AddMinutes(6);
        (await cache.ObterAsync("catalogo")).Should().Be("v1");
        var chamadas = cofre.Chamadas;

        // Logo depois: não volta ao cofre a cada leitura (nova tentativa só após um intervalo curto).
        relogio.AgoraUtc = Inicio.AddMinutes(6).AddSeconds(10);
        (await cache.ObterAsync("catalogo")).Should().Be("v1");
        cofre.Chamadas.Should().Be(chamadas);

        // Cofre de volta: valor novo.
        cofre.Falha = null;
        cofre.Valor = "v2";
        relogio.AgoraUtc = Inicio.AddMinutes(7);
        (await cache.ObterAsync("catalogo")).Should().Be("v2");

        // Fora por mais que a tolerância: falha (fail-closed), não serve segredo velho para sempre.
        cofre.Falha = new TimeoutException("cofre indisponível");
        relogio.AgoraUtc = Inicio.AddMinutes(7).Add(ProvedorSegredosCache.ToleranciaPadrao).AddMinutes(6);
        var leitura = async () => await cache.ObterAsync("catalogo");
        await leitura.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Cofre_Fora_Sem_Leitura_Anterior_Falha()
    {
        var cache = new ProvedorSegredosCache(new CofreLento { Falha = new TimeoutException("fora") }, new RelogioMutavel(Inicio), TimeSpan.FromMinutes(5));

        var leitura = async () => await cache.ObterAsync("catalogo");

        await leitura.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public void Criar_O_Contexto_Do_Catalogo_Nao_Le_O_Cofre()
    {
        var cofre = new CofreLento { Valor = "qualquer" };
        using var provedor = Compor("Server=127.0.0.1;Port=3306;Database=mysql;User Id=root;", cofre);
        using var escopo = provedor.CreateScope();

        var contexto = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        _ = contexto.Model; // contexto pronto para uso

        cofre.Chamadas.Should().Be(0, "a senha só é necessária ao abrir a conexão — e é lida de forma assíncrona");
        contexto.Database.GetConnectionString().Should().NotContain("qualquer", "a senha nunca fica na connection string configurada");
    }

    [SkippableFact]
    public async Task Abrir_A_Conexao_Le_A_Senha_Do_Cofre_E_Conecta_No_MySql_Real()
    {
        var admin = Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN");
        Skip.If(string.IsNullOrWhiteSpace(admin), "MySQL de teste indisponível.");
        var construtor = new MySqlConnectionStringBuilder(admin) { Database = "mysql" };
        var senha = construtor.Password;
        construtor.Password = string.Empty;
        var cofre = new CofreLento { Valor = senha };
        using var provedor = Compor(construtor.ConnectionString, cofre);

        await using (var escopo = provedor.CreateAsyncScope())
        {
            var contexto = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            (await contexto.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToListAsync()).Should().Equal(1);
        }

        // Abertura síncrona (rara: ferramentas) também funciona, servida pelo cache.
        using (var escopo = provedor.CreateScope())
        {
            var contexto = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            contexto.Database.OpenConnection();
            contexto.Database.CloseConnection();
        }

        cofre.Chamadas.Should().Be(1, "duas aberturas, uma busca no cofre (cache com TTL)");
    }

    private static ServiceProvider Compor(string conexaoSemSenha, CofreLento cofre)
    {
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Catalogo"] = conexaoSemSenha,
                ["BancoCatalogo:SenhaRef"] = "catalogo",
            })
            .Build();
        var servicos = new ServiceCollection();
        servicos.AddSingleton<IConfiguration>(configuracao);
        servicos.AddLogging();
        servicos.AddApplication();
        servicos.AddInfrastructure(configuracao, conexaoObrigatoria: true);

        // O cofre do teste, com o MESMO cache de produção por cima (última registração vence).
        servicos.AddSingleton<IProvedorSegredos>(sp => new ProvedorSegredosCache(cofre, sp.GetRequiredService<IRelogio>(), TimeSpan.FromMinutes(5)));
        return servicos.BuildServiceProvider();
    }

    private sealed class CofreLento : IProvedorSegredos
    {
        private int _chamadas;

        public string Valor { get; set; } = string.Empty;

        public TimeSpan Atraso { get; set; }

        public Exception? Falha { get; set; }

        public int Chamadas => Volatile.Read(ref _chamadas);

        public async ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _chamadas);
            if (Atraso > TimeSpan.Zero)
            {
                await Task.Delay(Atraso, cancellationToken);
            }

            return Falha is null ? Valor : throw Falha;
        }
    }

    private sealed class RelogioMutavel(DateTimeOffset agora) : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; set; } = agora;
    }
}
