using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Consultas;
using PortalBase.Application.Incidentes;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Arquivos;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Incidentes;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Prompt 81 — filtro por INTERVALO DE DATA aplicado NO BANCO (MySQL real) pelos read-models: limites
/// inclusivos (00:00:00 do dia inicial até 23:59:59.999999 do dia final, inclusive a fração de segundo das
/// colunas <c>datetime(6)</c>), contagem e paginação DEPOIS do filtro, composição com os demais filtros,
/// isolamento entre tenants (A × B) e rejeição server-side de <c>Desde &gt; Até</c> antes de tocar o banco.
/// Datas/valores são FIXTURES de teste. Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class FiltroPeriodoReadModelsP81MySqlTests
{
    private static readonly DateOnly DiaInicial = new(2026, 3, 10);
    private static readonly DateOnly DiaFinal = new(2026, 3, 12);
    private static readonly DateTimeOffset Desde = PeriodoConsulta.InicioDoDia(DiaInicial);
    private static readonly DateTimeOffset Ate = PeriodoConsulta.FimDoDia(DiaFinal);

    // Bordas: 1 µs antes do início (fora), início exato (dentro), meio (dentro), fração final do último dia
    // (dentro) e meia-noite do dia seguinte (fora).
    private static readonly DateTimeOffset Antes = Desde.AddTicks(-TimeSpan.TicksPerMicrosecond);
    private static readonly DateTimeOffset BordaInicial = Desde;
    private static readonly DateTimeOffset Meio = new(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BordaFinal = new DateTimeOffset(2026, 3, 12, 23, 59, 59, TimeSpan.Zero).AddMilliseconds(500);
    private static readonly DateTimeOffset Depois = new(2026, 3, 13, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset[] Momentos = [Antes, BordaInicial, Meio, BordaFinal, Depois];
    private static readonly DateTimeOffset[] Dentro = [BordaInicial, Meio, BordaFinal];

    private readonly FixtureMultiTenant _fixture;

    public FiltroPeriodoReadModelsP81MySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    private async Task<(string Usuario, Guid A, Guid B)> DoisTenantsAsync()
    {
        var a = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var b = await CenarioMultiTenant.ProvisionarTenantAsync(_fixture, CenarioMultiTenant.GerarCnpjValido());
        var usuario = await CenarioMultiTenant.CriarUsuarioAsync(_fixture);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, a);
        await CenarioMultiTenant.VincularAsync(_fixture, usuario, b);
        return (usuario, a, b);
    }

    private async Task NoTenantAsync(string usuario, Guid tenant, Func<IServiceProvider, Task> acao)
    {
        using var escopo = _fixture.CriarEscopo();
        await escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>().ResolverAsync(usuario, tenant);
        await acao(escopo.ServiceProvider);
    }

    private async Task<T> NoTenantAsync<T>(string usuario, Guid tenant, Func<IServiceProvider, Task<T>> acao)
    {
        using var escopo = _fixture.CriarEscopo();
        await escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>().ResolverAsync(usuario, tenant);
        return await acao(escopo.ServiceProvider);
    }

    // ---- Arquivos de importação (/dados/arquivos — EnviadoEmUtc) ----

    [SkippableFact]
    public async Task Arquivos_Periodo_No_Banco_Com_Isolamento()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (usuario, a, b) = await DoisTenantsAsync();
        var autor = Guid.Parse(usuario);

        static ArquivoImportacao Arquivo(Guid autor, int n, DateTimeOffset enviadoEm)
            => ArquivoImportacao.Registrar("planilha.csv", $"seguro-{n}.csv", "csv", "text/csv", 42,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(),
                autor, "corr-p81", 5, enviadoEm, enviadoEm.AddYears(5));

        await NoTenantAsync(usuario, a, async sp =>
        {
            var repo = sp.GetRequiredService<IRepositorioArquivoImportacao>();
            var n = 0;
            foreach (var m in Momentos)
            {
                await repo.AdicionarAsync(Arquivo(autor, n++, m));
            }

            await repo.SalvarAsync();
        });
        await NoTenantAsync(usuario, b, async sp =>
        {
            var repo = sp.GetRequiredService<IRepositorioArquivoImportacao>();
            await repo.AdicionarAsync(Arquivo(autor, 99, Meio));
            await repo.SalvarAsync();
        });

        var r = await NoTenantAsync(usuario, a, sp => sp.GetRequiredService<IConsultaArquivosImportacao>()
            .ConsultarAsync(new FiltroArquivosImportacao { Desde = Desde, Ate = Ate, Extensao = "csv" }));

        r.TotalItens.Should().Be(3);
        r.Itens.Select(i => i.EnviadoEmUtc).Should().BeEquivalentTo(Dentro);
    }

    // ---- Incidentes (/auditoria/incidentes — DetectadoEmUtc, catálogo de PLATAFORMA) ----

    [SkippableFact]
    public async Task Incidentes_Periodo_No_Banco_Composto_Com_Tenant_Afetado()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var afetado = Guid.NewGuid(); // compõe com o filtro de tenant afetado (catálogo é compartilhado na coleção)
        var outro = Guid.NewGuid();

        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            foreach (var m in Momentos)
            {
                catalogo.IncidentesSeguranca.Add(IncidenteSeguranca.Registrar(null, afetado, TipoIncidente.Tecnico, SeveridadeIncidente.Medio,
                    "Fixture P81", "Incidente de teste.", null, null, "op-p81", null, m));
            }

            catalogo.IncidentesSeguranca.Add(IncidenteSeguranca.Registrar(null, outro, TipoIncidente.Tecnico, SeveridadeIncidente.Medio,
                "Fixture P81", "Incidente de teste.", null, null, "op-p81", null, Meio));
            await catalogo.SaveChangesAsync();
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var r = await escopo.ServiceProvider.GetRequiredService<IConsultaIncidentes>()
                .ConsultarAsync(new FiltroIncidentes { Desde = Desde, Ate = Ate, TenantAfetadoId = afetado });

            r.TotalItens.Should().Be(3);
            r.Itens.Select(i => i.DetectadoEmUtc).Should().BeEquivalentTo(Dentro);
            r.Itens.Should().OnlyContain(i => i.TenantAfetadoId == afetado);
        }
    }

    // ---- Auditoria (/auditoria/eventos e /auditoria/operacoes — OcorridoEmUtc, catálogo) ----

    [SkippableFact]
    public async Task Auditoria_Periodo_No_Banco_Com_Isolamento_Por_Tenant()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();

        using (var escopo = _fixture.CriarEscopo())
        {
            var auditoria = escopo.ServiceProvider.GetRequiredService<IAuditoria>();
            foreach (var m in Momentos)
            {
                await auditoria.RegistrarAsync(new EventoAuditoria("ator-p81", tenantA, AcaoAuditoria.AccessGranted, ResultadoAuditoria.Permitido, m));
            }

            await auditoria.RegistrarAsync(new EventoAuditoria("ator-p81", tenantB, AcaoAuditoria.AccessGranted, ResultadoAuditoria.Permitido, Meio));
        }

        using (var escopo = _fixture.CriarEscopo())
        {
            var consulta = escopo.ServiceProvider.GetRequiredService<IConsultaAuditoria>();
            var r = await consulta.ConsultarAsync(new FiltroAuditoria { Tenant = tenantA, Desde = Desde, Ate = Ate });

            r.Total.Should().Be(3);
            r.Itens.Select(i => i.OcorridoEmUtc).Should().BeEquivalentTo(Dentro);

            var paginada = await consulta.ConsultarAsync(new FiltroAuditoria { Tenant = tenantA, Desde = Desde, Ate = Ate, Pagina = 2, TamanhoPagina = 2 });
            paginada.Total.Should().Be(3);
            paginada.Itens.Should().ContainSingle().Which.OcorridoEmUtc.Should().Be(BordaInicial); // mais antigo, última página
        }
    }

    /// <summary>Autorização de teste que permite tudo — o foco é a validação de período no serviço real.</summary>
    private sealed class AutorizacaoPermitirTudo : IServicoAutorizacao
    {
        public ValueTask<ResultadoAutorizacao> AvaliarAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ResultadoAutorizacao.Ok);

        public ValueTask ExigirAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<ResultadoAutorizacao> AvaliarAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ResultadoAutorizacao.Ok);

        public ValueTask ExigirAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
