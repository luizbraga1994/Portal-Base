using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Auditoria;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Trilha de auditoria contra MySQL real (Prompt 05): campos/correlação/UTC, integridade
/// (tamper), sanitização, append-only (grants), isolamento por tenant, retenção e concorrência.
/// Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class AuditoriaTrailTests
{
    private readonly FixtureMultiTenant _fixture;

    public AuditoriaTrailTests(FixtureMultiTenant fixture) => _fixture = fixture;

    private async Task RegistrarAsync(EventoAuditoria evento, Action<IContextoAuditoriaMutavel>? correlacao = null)
    {
        using var escopo = _fixture.CriarEscopo();
        var ctx = escopo.ServiceProvider.GetRequiredService<IContextoAuditoriaMutavel>();
        ctx.DefinirOrigem("Web", TipoAtor.Usuario);
        correlacao?.Invoke(ctx);
        var auditoria = escopo.ServiceProvider.GetRequiredService<IAuditoria>();
        await auditoria.RegistrarAsync(evento);
    }

    // Campos obrigatórios + correlação + UTC.
    [SkippableFact]
    public async Task Persiste_Campos_Correlacao_E_Utc()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = Guid.NewGuid().ToString();
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        var quando = DateTimeOffset.UtcNow;

        await RegistrarAsync(
            new EventoAuditoria("ator-1", tenant, AcaoAuditoria.LoginSucceeded, ResultadoAuditoria.Permitido, quando)
            {
                TipoRecurso = "Usuario",
                RecursoId = "ator-1",
            },
            ctx =>
            {
                ctx.DefinirCorrelacao(corr, "trace-xyz");
                ctx.DefinirOrigemHttp("203.0.113.9", "UA/1.0");
            });

        using var escopo = _fixture.CriarEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var reg = await db.RegistrosAuditoria.AsNoTracking().FirstAsync(r => r.CorrelationId == corr);

        reg.Tenant.Should().Be(tenant);
        reg.PrincipalId.Should().Be("ator-1");
        reg.Operacao.Should().Be(AcaoAuditoria.LoginSucceeded);
        reg.TipoAtor.Should().Be((int)TipoAtor.Usuario);
        reg.TraceId.Should().Be("trace-xyz");
        reg.IpAddress.Should().Be("203.0.113.9");
        reg.OcorridoEmUtc.Should().BeCloseTo(quando.UtcDateTime, TimeSpan.FromSeconds(2));
        reg.IntegridadeHash.Should().NotBeNullOrEmpty();
    }

    // Integridade: o hash confere; adulterar qualquer campo o invalida (seção 14).
    [SkippableFact]
    public async Task Integridade_Detecta_Adulteracao()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        await RegistrarAsync(
            new EventoAuditoria("ator-x", Guid.NewGuid().ToString(), AcaoAuditoria.AccessGranted, ResultadoAuditoria.Permitido, DateTimeOffset.UtcNow),
            ctx => ctx.DefinirCorrelacao(corr, null));

        using var escopo = _fixture.CriarEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var integridade = escopo.ServiceProvider.GetRequiredService<IntegridadeAuditoria>();
        var reg = await db.RegistrosAuditoria.AsNoTracking().FirstAsync(r => r.CorrelationId == corr);

        (await integridade.VerificarAsync(reg)).Should().BeTrue();

        reg.PrincipalId = "atacante"; // adultera em memória
        (await integridade.VerificarAsync(reg)).Should().BeFalse();
    }

    // Sanitização: segredos em old/new nunca são persistidos em claro (seções 6, 7).
    [SkippableFact]
    public async Task Sanitiza_Valores_Sensiveis()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        await RegistrarAsync(
            new EventoAuditoria("ator-s", Guid.NewGuid().ToString(), AcaoAuditoria.UserUpdated, ResultadoAuditoria.Permitido, DateTimeOffset.UtcNow)
            {
                ValoresNovos = new Dictionary<string, string?>
                {
                    ["senha"] = "SENHA-SECRETA-XYZ",
                    ["token"] = "TOKEN-SECRETO-XYZ",
                    ["papel"] = "Analista",
                },
            },
            ctx => ctx.DefinirCorrelacao(corr, null));

        using var escopo = _fixture.CriarEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var reg = await db.RegistrosAuditoria.AsNoTracking().FirstAsync(r => r.CorrelationId == corr);

        reg.ValoresNovos.Should().NotContain("SENHA-SECRETA-XYZ");
        reg.ValoresNovos.Should().NotContain("TOKEN-SECRETO-XYZ");
        reg.ValoresNovos.Should().Contain("Analista"); // valor não sensível preservado
        reg.ValoresNovos.Should().Contain(SanitizacaoAuditoria.Redigido);
    }

    // Append-only: uma credencial de runtime com apenas SELECT,INSERT não consegue DELETE (seção 14).
    [SkippableFact]
    public async Task Runtime_Nao_Consegue_Deletar_Auditoria()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var conexaoAdmin = Environment.GetEnvironmentVariable("PORTALBASE_TEST_MYSQL_ADMIN")!;
        var admin = new MySqlConnectionStringBuilder(conexaoAdmin);
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var usuario = $"pb_audit_ro_{sufixo}";
        var senha = $"Aud1_{sufixo}";

        await using var conexao = new MySqlConnection(conexaoAdmin);
        await conexao.OpenAsync();
        await ExecutarAsync(conexao, $"CREATE USER '{usuario}'@'%' IDENTIFIED BY '{senha}'");
        try
        {
            // Modelo append-only: somente SELECT e INSERT na tabela de auditoria — sem UPDATE/DELETE.
            await ExecutarAsync(conexao, $"GRANT SELECT, INSERT ON `{_fixture.CatalogoDb}`.`registro_auditoria` TO '{usuario}'@'%'");

            var conexaoRuntime = new MySqlConnectionStringBuilder(conexaoAdmin)
            {
                UserID = usuario,
                Password = senha,
                Database = _fixture.CatalogoDb,
            }.ConnectionString;

            await using var runtime = new MySqlConnection(conexaoRuntime);
            await runtime.OpenAsync();

            var deletar = async () => await ExecutarAsync(runtime, "DELETE FROM registro_auditoria WHERE id > 0");
            await deletar.Should().ThrowAsync<MySqlException>(); // DELETE negado (append-only)

            var atualizar = async () => await ExecutarAsync(runtime, "UPDATE registro_auditoria SET resultado='X' WHERE id > 0");
            await atualizar.Should().ThrowAsync<MySqlException>(); // UPDATE negado
        }
        finally
        {
            await ExecutarSeguroAsync(conexao, $"DROP USER IF EXISTS '{usuario}'@'%'");
        }
    }

    // Isolamento: o admin de A consulta a auditoria de A e NÃO vê a de B (seção 20).
    [SkippableFact]
    public async Task Consulta_Isolada_Por_Tenant()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var (admin, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, admin, a, PapelConhecido.Administrador);

        await RegistrarAsync(new EventoAuditoria("x", a.ToString(), AcaoAuditoria.AccessGranted, ResultadoAuditoria.Permitido, DateTimeOffset.UtcNow));
        await RegistrarAsync(new EventoAuditoria("y", b.ToString(), AcaoAuditoria.AccessGranted, ResultadoAuditoria.Permitido, DateTimeOffset.UtcNow));

        using var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, admin, mfaSatisfeito: false);
        var consulta = escopo.ServiceProvider.GetRequiredService<ConsultaAuditoriaAutorizada>();
        var pagina = await consulta.ConsultarAsync(a, new FiltroAuditoria { Tenant = a.ToString() });

        pagina.Itens.Should().OnlyContain(r => r.Tenant == a.ToString());
        pagina.Itens.Should().NotContain(r => r.Tenant == b.ToString());
    }

    // Leitor não consulta a trilha; consultar B (não autorizado) é negado; tenant desabilitado é negado.
    [SkippableFact]
    public async Task Consulta_Negada_Para_Leitor_E_Cross_Tenant_E_Desabilitado()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var a = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioIdentidade.CriarTenantProntoAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());

        var (leitor, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, leitor, a, PapelConhecido.Leitor);
        using (var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, leitor, false))
        {
            var consulta = escopo.ServiceProvider.GetRequiredService<ConsultaAuditoriaAutorizada>();
            var acao = async () => await consulta.ConsultarAsync(a, new FiltroAuditoria { Tenant = a.ToString() });
            await acao.Should().ThrowAsync<AutorizacaoNegadaException>(); // Leitor sem auditoria.visualizar
        }

        var (admin, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaForte#2026");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, admin, a, PapelConhecido.Administrador);
        using (var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, admin, false))
        {
            var consulta = escopo.ServiceProvider.GetRequiredService<ConsultaAuditoriaAutorizada>();
            var manipular = async () => await consulta.ConsultarAsync(b, new FiltroAuditoria { Tenant = b.ToString() });
            await manipular.Should().ThrowAsync<TenantNaoAutorizadoException>(); // admin de A não consulta B
        }

        await CenarioIdentidade.DesabilitarTenantAsync(_fixture, a);
        using (var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, admin, false))
        {
            var consulta = escopo.ServiceProvider.GetRequiredService<ConsultaAuditoriaAutorizada>();
            var desabilitado = async () => await consulta.ConsultarAsync(a, new FiltroAuditoria { Tenant = a.ToString() });
            await desabilitado.Should().ThrowAsync<TenantNaoAutorizadoException>(); // tenant desabilitado
        }
    }

    // Concorrência: muitos eventos simultâneos geram ids únicos, todos persistidos (seção 37).
    [SkippableFact]
    public async Task Concorrencia_Gera_Ids_Unicos()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenant = Guid.NewGuid().ToString();
        var corr = "conc-" + Guid.NewGuid().ToString("N");

        var tarefas = Enumerable.Range(0, 12).Select(_ => RegistrarAsync(
            new EventoAuditoria("ator", tenant, AcaoAuditoria.AuthorizationDenied, ResultadoAuditoria.Negado, DateTimeOffset.UtcNow),
            ctx => ctx.DefinirCorrelacao(corr, null)));
        await Task.WhenAll(tarefas);

        using var escopo = _fixture.CriarEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var ids = await db.RegistrosAuditoria.AsNoTracking().Where(r => r.CorrelationId == corr).Select(r => r.Id).ToListAsync();

        ids.Should().HaveCount(12);
        ids.Distinct().Should().HaveCount(12);
    }

    private static async Task ExecutarAsync(MySqlConnection conexao, string sql)
    {
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task ExecutarSeguroAsync(MySqlConnection conexao, string sql)
    {
        try
        {
            await ExecutarAsync(conexao, sql);
        }
        catch (MySqlException)
        {
            // limpeza best-effort
        }
    }
}
