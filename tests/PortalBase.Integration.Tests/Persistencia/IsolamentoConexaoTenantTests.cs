using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Excecoes;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Opcoes;
using PortalBase.Infrastructure.Persistencia;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Prompt 48 — isolamento database-per-tenant na camada de RESOLUÇÃO DE CONEXÃO (não requer MySQL real).
/// Prova que o database vem de fonte confiável (config + catálogo), nunca do cliente; que nomes de base
/// hostis/reservados são rejeitados (anti connection-string/db-name injection); que o TLS é EXIGIDO por padrão
/// (§18, fecha o gap do P35 <c>Preferred</c>); e que a fábrica é FAIL-CLOSED sem contexto autorizado.
/// O isolamento FÍSICO (dois bancos reais) exige MySQL e está nos testes gated (BLOCKED sem MySQL).
/// </summary>
public sealed class IsolamentoConexaoTenantTests
{
    private static readonly OpcoesBancoTenant Opcoes = new()
    {
        Servidor = "db.interno.example",
        Porta = 3306,
        RuntimeUsuario = "pb_app_ro",
        RuntimeSenhaRef = "runtime-ref",
        AdminUsuario = "pb_admin",
        AdminSenhaRef = "admin-ref",
        // SslMode default = Required (não configurado aqui)
    };

    private static ConstrutorConexaoTenant Construtor()
    {
        var segredos = new ProvedorSegredosFake();
        segredos.Valores["runtime-ref"] = "senha-runtime";
        segredos.Valores["admin-ref"] = "senha-admin";
        return new ConstrutorConexaoTenant(Options.Create(Opcoes), segredos);
    }

    private static ProtecaoOperacaoEmVoo Protecao() => new(NullLogger<ProtecaoOperacaoEmVoo>.Instance);

    [Fact] // a base do tenant vem da config/catálogo; host/porta/usuário/senha NUNCA do cliente
    public async Task Runtime_Usa_Config_E_Base_Validada()
    {
        var conexao = await Construtor().ConstruirRuntimeAsync("tenant_abc123");
        var b = new MySqlConnectionStringBuilder(conexao);

        b.Database.Should().Be("tenant_abc123");
        b.Server.Should().Be("db.interno.example");
        b.UserID.Should().Be("pb_app_ro");     // usuário de MENOR privilégio (runtime), não o admin
        b.Port.Should().Be(3306);
    }

    [Fact] // §18 — TLS EXIGIDO por padrão (nunca Preferred/Disabled), fecha o gap do P35
    public async Task Tls_Exigido_Por_Padrao()
    {
        var b = new MySqlConnectionStringBuilder(await Construtor().ConstruirRuntimeAsync("tenant_x"));
        b.SslMode.Should().Be(MySqlSslMode.Required);
        b.SslMode.Should().NotBe(MySqlSslMode.Preferred);
    }

    [Theory] // nomes de base HOSTIS/inválidos são rejeitados ANTES de compor a conexão (anti-injeção)
    [InlineData("Tenant_A")]                         // maiúsculas fora do padrão
    [InlineData("tenant a")]                          // espaço
    [InlineData("tenant`;drop")]                      // backtick/;
    [InlineData("tenant;select")]                     // ;
    [InlineData("tenant--x")]                         // hífen
    [InlineData("../outro")]                          // path/escape
    [InlineData("")]                                  // vazio
    public void NomeBase_Hostil_Rejeitado(string nomeHostil)
    {
        var acao = () => ConstrutorConexaoTenant.ValidarNomeBase(nomeHostil);
        acao.Should().Throw<Exception>().Which.Should().Match(e => e is EntradaInvalidaException || e is ArgumentException);
    }

    [Theory] // esquemas de sistema NUNCA podem ser base de tenant (anti-escalonamento)
    [InlineData("mysql")]
    [InlineData("information_schema")]
    [InlineData("performance_schema")]
    [InlineData("sys")]
    public void Esquema_Reservado_Rejeitado(string reservado)
    {
        var acao = () => ConstrutorConexaoTenant.ValidarNomeBase(reservado);
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact] // admin vs runtime: usuários distintos, ambos da config (nunca do caller)
    public async Task Admin_E_Runtime_Usuarios_Distintos_Da_Config()
    {
        var construtor = Construtor();
        var runtime = new MySqlConnectionStringBuilder(await construtor.ConstruirRuntimeAsync("tenant_z"));
        var admin = new MySqlConnectionStringBuilder(await construtor.ConstruirAdminAsync("tenant_z"));

        runtime.UserID.Should().Be("pb_app_ro");
        admin.UserID.Should().Be("pb_admin");
        runtime.UserID.Should().NotBe(admin.UserID);
    }

    [Fact] // P62-R1 — admin ligado a UMA base de tenant é pontual: SEM pool (não retém sessão privilegiada ociosa)
    public async Task Admin_Na_Base_Do_Tenant_Nao_Usa_Pool()
    {
        var admin = new MySqlConnectionStringBuilder(await Construtor().ConstruirAdminAsync("tenant_z"));

        admin.Pooling.Should().BeFalse("cada base provisionada reteria uma sessão admin ociosa no pool");
        admin.Database.Should().Be("tenant_z");
    }

    [Fact] // P62-R1 — admin de nível servidor e runtime seguem com pool limitado pela configuração
    public async Task Admin_Servidor_E_Runtime_Usam_Pool_Configurado()
    {
        var construtor = Construtor();
        var servidor = new MySqlConnectionStringBuilder(await construtor.ConstruirAdminAsync(nomeBase: null));
        var runtime = new MySqlConnectionStringBuilder(await construtor.ConstruirRuntimeAsync("tenant_z"));

        servidor.Pooling.Should().BeTrue();
        runtime.Pooling.Should().BeTrue();
        runtime.MaximumPoolSize.Should().Be(Opcoes.MaximoPool);
        runtime.ConnectionLifeTime.Should().Be(Opcoes.VidaConexaoSegundos);
    }

    // ---- FabricaContextoTenant: FAIL-CLOSED ----

    [Fact] // contexto NÃO autorizado ⇒ não abre contexto (fail-closed)
    public async Task Fabrica_Sem_Autorizacao_FailClosed()
    {
        var fabrica = new FabricaContextoTenant(Construtor(), Protecao());
        var acao = async () => await fabrica.CriarAsync(new TenantFake(autorizado: false, "tenant_a"));
        await acao.Should().ThrowAsync<TenantContextAusenteException>();
    }

    [Fact] // autorizado mas SEM nome de base ⇒ fail-closed (nunca banco default)
    public async Task Fabrica_Sem_NomeBase_FailClosed()
    {
        var fabrica = new FabricaContextoTenant(Construtor(), Protecao());
        var acao = async () => await fabrica.CriarAsync(new TenantFake(autorizado: true, nomeBase: null));
        await acao.Should().ThrowAsync<TenantContextAusenteException>();
    }

    [Fact] // autorizado com base válida ⇒ cria o contexto apontando para a base do tenant (não conecta)
    public async Task Fabrica_Autorizado_Cria_Contexto_Da_Base()
    {
        var fabrica = new FabricaContextoTenant(Construtor(), Protecao());
        await using var contexto = await fabrica.CriarAsync(new TenantFake(autorizado: true, "tenant_valido1"));
        contexto.Should().NotBeNull();
    }

    private sealed class TenantFake(bool autorizado, string? nomeBase) : ITenantContext
    {
        public EstadoTenant Estado => autorizado ? EstadoTenant.Autorizado : EstadoTenant.NaoResolvido;

        public string? PrincipalId => "u";

        public Guid? TenantId => autorizado ? Guid.NewGuid() : null;

        public string? NomeBaseFisica => nomeBase;

        public string? ReferenciaSegredoConexao => null;

        public bool EstaAutorizado => autorizado;
    }
}
