using FluentAssertions;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Provisionamento;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Catalogo;

/// <summary>
/// Atualização da estrutura das bases dos contribuintes pela tela da plataforma: só o Administrador da plataforma; só as
/// bases desatualizadas; desabilitado não é tocado; uma falha não interrompe as demais e não expõe detalhe técnico além do
/// que o provisionador já sanitiza.
/// </summary>
public sealed class AtualizacaoSchemaContribuintesTests
{
    private const string Atual = "20261007180844_TenantDeclaracaoEventoAuxiliar";
    private const string Antiga = "20260918214026_TenantMarcaVisual";
    private static readonly DateTimeOffset Agora = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static ResumoContribuinte Base(string nome, EstadoProvisionamento estado, string? migracao)
        => new(Guid.NewGuid(), nome, "11222333000181", $"{nome} Ltda", estado, false, Agora, migracao);

    [Fact]
    public async Task Situacao_Compara_Cada_Base_Com_A_Versao_Do_Codigo()
    {
        var c = new Cenario(
            Base("ACME", EstadoProvisionamento.Ativo, Atual),
            Base("STAKE", EstadoProvisionamento.Ativo, Antiga),
            Base("NOVA", EstadoProvisionamento.Pendente, null));

        var situacao = await c.Servico.ObterSituacaoAsync();

        situacao.VersaoEsperada.Should().Be(Atual);
        situacao.Bases.Select(b => b.Nome).Should().BeEquivalentTo(["ACME", "STAKE"], "base ainda não provisionada não entra");
        situacao.Desatualizadas.Should().Be(1);
        situacao.Bases.Single(b => b.Nome == "STAKE").Atualizada.Should().BeFalse();
    }

    [Fact]
    public async Task Sem_Permissao_De_Plataforma_Nega_Ler_E_Atualizar()
    {
        var c = new Cenario(Base("STAKE", EstadoProvisionamento.Ativo, Antiga));
        c.Plataforma.Permitir = false;

        await c.Servico.Invoking(s => s.ObterSituacaoAsync()).Should().ThrowAsync<AutorizacaoNegadaException>();
        await c.Servico.Invoking(s => s.AtualizarDesatualizadasAsync()).Should().ThrowAsync<AutorizacaoNegadaException>();
        c.Provisionador.Chamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Sem_Usuario_Autenticado_Nao_Atualiza()
    {
        var c = new Cenario(Base("STAKE", EstadoProvisionamento.Ativo, Antiga));
        c.Seguranca.PrincipalId = null;

        await c.Servico.Invoking(s => s.AtualizarDesatualizadasAsync()).Should().ThrowAsync<NaoAutenticadoException>();
        c.Provisionador.Chamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Atualiza_So_As_Desatualizadas_Ignora_Desabilitada_E_Segue_Apos_Falha()
    {
        var atualizada = Base("ACME", EstadoProvisionamento.Ativo, Atual);
        var antiga = Base("STAKE", EstadoProvisionamento.Ativo, Antiga);
        var falha = Base("BETA", EstadoProvisionamento.Pronto, Antiga);
        var desabilitada = Base("OFF", EstadoProvisionamento.Desabilitado, Antiga);
        var c = new Cenario(falha, atualizada, desabilitada, antiga);
        c.Provisionador.Falhar.Add(falha.Id);

        var resultados = await c.Servico.AtualizarDesatualizadasAsync();

        c.Provisionador.Chamadas.Should().BeEquivalentTo([falha.Id, antiga.Id], "a base em dia e a desabilitada não são tocadas");
        c.Provisionador.Solicitante.Should().Be("plataforma-1");
        resultados.Should().HaveCount(3);
        resultados.Single(r => r.TenantId == antiga.Id).Atualizada.Should().BeTrue();
        resultados.Single(r => r.TenantId == falha.Id).Should().Match<ResultadoAtualizacaoSchema>(r => !r.Atualizada && r.Motivo == "Falha ao aplicar migrations.");
        resultados.Single(r => r.TenantId == desabilitada.Id).Motivo.Should().StartWith("Desabilitado");
    }

    [Fact]
    public async Task Tudo_Em_Dia_Nao_Chama_O_Provisionador()
    {
        var c = new Cenario(Base("ACME", EstadoProvisionamento.Ativo, Atual));

        (await c.Servico.AtualizarDesatualizadasAsync()).Should().BeEmpty();
        c.Provisionador.Chamadas.Should().BeEmpty();
    }

    private sealed class Cenario
    {
        public Cenario(params ResumoContribuinte[] bases)
        {
            Servico = new AtualizacaoSchemaContribuintes(Plataforma, Seguranca, new ConsultaFixa(bases), Provisionador, new VersaoFixa());
        }

        public AutorizacaoPlataformaFake Plataforma { get; } = new();

        public ContextoSegurancaFake Seguranca { get; } = new() { PrincipalId = "plataforma-1" };

        public ProvisionadorFake Provisionador { get; } = new();

        public AtualizacaoSchemaContribuintes Servico { get; }
    }

    private sealed class ProvisionadorFake : IProvisionadorTenant
    {
        public List<Guid> Chamadas { get; } = [];

        public HashSet<Guid> Falhar { get; } = [];

        public string? Solicitante { get; private set; }

        public ValueTask<ResultadoProvisionamento> ProvisionarAsync(Guid tenantId, string solicitanteId, CancellationToken cancellationToken = default)
        {
            Chamadas.Add(tenantId);
            Solicitante = solicitanteId;
            return ValueTask.FromResult(Falhar.Contains(tenantId)
                ? new ResultadoProvisionamento(tenantId, EstadoProvisionamento.Pronto, Antiga, "Falha ao aplicar migrations.")
                : new ResultadoProvisionamento(tenantId, EstadoProvisionamento.Ativo, Atual, null));
        }
    }

    private sealed class ConsultaFixa(IReadOnlyList<ResumoContribuinte> bases) : IConsultaContribuintesPlataforma
    {
        public Task<IReadOnlyList<ResumoContribuinte>> ListarAsync(int maximo, CancellationToken cancellationToken = default)
            => Task.FromResult(bases);
    }

    private sealed class VersaoFixa : IVersaoSchemaTenant
    {
        public string? UltimaMigracaoDisponivel => Atual;
    }
}
