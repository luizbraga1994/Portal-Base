using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PortalBase.Domain.Incidentes;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;
using Testcontainers.MySql;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Persistência de INCIDENTES DE SEGURANÇA (Prompt 30, seções 45-47) no CATÁLOGO em MySQL real (Testcontainers):
/// migração das tabelas, round-trip do agregado + TIMELINE append-only e a UNICIDADE do código garantida pelo
/// BANCO. Pulado sem Docker; executa no CI.
/// </summary>
public sealed class IncidentesMySqlTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static IncidenteSeguranca Novo(string codigo)
        => IncidenteSeguranca.Registrar(codigo, Guid.NewGuid(), TipoIncidente.Tecnico, SeveridadeIncidente.Alto,
            "Acesso anômalo", "Investigar tentativas.", "Web", "Autenticacao", "op-1", "corr-1", T0);

    [SkippableFact]
    public async Task Persiste_Incidente_Com_Timeline_E_Unicidade()
    {
        Skip.IfNot(DockerDisponivel.Verificar(), "Docker indisponível neste ambiente.");

        await using var container = new MySqlBuilder("mysql:8.4").Build();
        await container.StartAsync();

        var opcoes = new DbContextOptionsBuilder<PortalBaseCatalogoDbContext>()
            .UseMySQL(container.GetConnectionString())
            .Options;

        await using (var contexto = new PortalBaseCatalogoDbContext(opcoes))
        {
            await contexto.Database.MigrateAsync();

            var incidente = Novo("INC-TESTE-1");
            incidente.IniciarAnalise("op-1", "análise", T0.AddMinutes(1));
            incidente.RegistrarContencao("op-1", "contido", T0.AddMinutes(2));
            incidente.Encerrar("dpo-1", "resolvido", T0.AddMinutes(3));
            contexto.IncidentesSeguranca.Add(incidente);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = new PortalBaseCatalogoDbContext(opcoes))
        {
            var lido = await contexto.IncidentesSeguranca
                .Include(x => x.Timeline)
                .AsNoTracking()
                .FirstAsync(x => x.Codigo == "INC-TESTE-1");
            lido.Status.Should().Be(StatusIncidente.Encerrado);
            lido.Timeline.Should().HaveCount(4); // detecção + análise + contenção + encerramento (append-only)
            lido.Timeline.Select(t => t.Sequencia).Should().BeInAscendingOrder();
        }

        await using (var contexto = new PortalBaseCatalogoDbContext(opcoes))
        {
            contexto.IncidentesSeguranca.Add(Novo("INC-TESTE-1")); // mesmo código ⇒ UNIQUE
            var acao = async () => await contexto.SaveChangesAsync();
            await acao.Should().ThrowAsync<DbUpdateException>();
        }
    }
}
