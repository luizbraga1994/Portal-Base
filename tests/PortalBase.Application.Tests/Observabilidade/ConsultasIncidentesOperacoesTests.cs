using FluentAssertions;
using PortalBase.Application.Consultas;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Incidentes;
using PortalBase.Application.Observabilidade;
using PortalBase.Application.Tests.Dobras;

namespace PortalBase.Application.Tests.Observabilidade;

/// <summary>Read-models de Incidentes (catálogo/plataforma) e Operações (observabilidade) — Prompt 36, §27-28, §60-61.</summary>
public sealed class ConsultasIncidentesOperacoesTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    // ---- Incidentes (autorização de PLATAFORMA) ----

    [Fact]
    public async Task Incidentes_Sem_Permissao_De_Plataforma_Nega()
    {
        var servico = new ConsultaIncidentesAutorizada(new AutorizacaoPlataformaFake { Permitir = false }, new ConsultaIncidentesFake(), new RepositorioIncidentesFake());
        var acao = async () => await servico.ConsultarAsync(new FiltroIncidentes());
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [Fact]
    public async Task Incidentes_Autorizado_Normaliza_Paginacao()
    {
        var consulta = new ConsultaIncidentesFake();
        var servico = new ConsultaIncidentesAutorizada(new AutorizacaoPlataformaFake(), consulta, new RepositorioIncidentesFake());

        await servico.ConsultarAsync(new FiltroIncidentes { Pagina = 0, TamanhoPagina = 999_999 });

        consulta.UltimoFiltro!.Pagina.Should().Be(1);
        consulta.UltimoFiltro!.TamanhoPagina.Should().Be(Paginacao.TamanhoMaximo);
    }

    // ---- Prompt 73: ObterAsync (detalhe por Id de primeira classe) — autorização de PLATAFORMA, fail-closed ----

    [Fact]
    public async Task Incidente_Obter_Sem_Permissao_De_Plataforma_Nega()
    {
        var servico = new ConsultaIncidentesAutorizada(new AutorizacaoPlataformaFake { Permitir = false }, new ConsultaIncidentesFake(), new RepositorioIncidentesFake());
        var acao = async () => await servico.ObterAsync(Guid.NewGuid());
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [Fact]
    public async Task Incidente_Obter_Nao_Encontrado_Retorna_Null()
    {
        var servico = new ConsultaIncidentesAutorizada(new AutorizacaoPlataformaFake(), new ConsultaIncidentesFake(), new RepositorioIncidentesFake());
        (await servico.ObterAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ---- Linha do tempo do incidente (gestão pela tela) ----

    [Fact]
    public async Task Timeline_Sem_Permissao_Nega_E_Com_Permissao_Vem_Em_Ordem()
    {
        var repositorio = new RepositorioIncidentesFake();
        var incidente = PortalBase.Domain.Incidentes.IncidenteSeguranca.Registrar(null, null, PortalBase.Domain.Incidentes.TipoIncidente.Tecnico,
            PortalBase.Domain.Incidentes.SeveridadeIncidente.Medio, "Título", "Descrição", null, null, "ator-1", null, Agora);
        incidente.IniciarAnalise("ator-2", "Coletando logs.", Agora.AddMinutes(5));
        incidente.AdicionarNota("ator-2", "Nota.", Agora.AddMinutes(6));
        repositorio.Itens.Add(incidente);

        var negar = async () => await new ConsultaIncidentesAutorizada(new AutorizacaoPlataformaFake { Permitir = false }, new ConsultaIncidentesFake(), repositorio)
            .ObterTimelineAsync(incidente.Id);
        await negar.Should().ThrowAsync<AutorizacaoNegadaException>();

        var servico = new ConsultaIncidentesAutorizada(new AutorizacaoPlataformaFake(), new ConsultaIncidentesFake(), repositorio);
        var timeline = await servico.ObterTimelineAsync(incidente.Id);
        timeline.Select(t => t.Sequencia).Should().Equal(1, 2, 3);
        timeline[1].Tipo.Should().Be(PortalBase.Domain.Incidentes.TipoEntradaTimeline.Analise);
        timeline[1].AtorId.Should().Be("ator-2");
        timeline[1].Descricao.Should().Be("Coletando logs.");
        (await servico.ObterTimelineAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    // ---- Operações (snapshot de observabilidade) ----

    private static ConsultaOperacoesAutorizada Operacoes(
        AutorizacaoPlataformaFake autorizacao, RegistroSaudeWorkersFake workers, RegistroUltimaIntegracaoFake integracoes,
        RepositorioSaudeWorkersPersistidaFake? persistida = null)
        => new(autorizacao, workers, persistida ?? new RepositorioSaudeWorkersPersistidaFake(), integracoes, new OpcoesObservabilidade(), new RelogioFixo(Agora));

    [Fact]
    public async Task Operacoes_Sem_Permissao_Nega()
    {
        var servico = Operacoes(new AutorizacaoPlataformaFake { Permitir = false }, new RegistroSaudeWorkersFake(), new RegistroUltimaIntegracaoFake());
        var acao = async () => await servico.ObterPainelAsync();
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [Fact]
    public async Task Operacoes_Deriva_Estagnacao_E_Degradacao()
    {
        var workers = new RegistroSaudeWorkersFake();
        // Ativo, batida há 10 min (> limite 300s) => estagnado.
        workers.Estados.Add(new EstadoSaudeWorker("outbox", true, Agora.AddHours(-1), Agora.AddMinutes(-10), Agora.AddMinutes(-10), 5, null, null, 0, 0));
        // Ativo, batida há 10 s => saudável.
        workers.Estados.Add(new EstadoSaudeWorker("transmissao", true, Agora.AddHours(-1), Agora.AddSeconds(-10), Agora.AddSeconds(-10), 3, null, null, 0, 0));

        var integracoes = new RegistroUltimaIntegracaoFake();
        // Último sucesso há 2 h (> limite 3600s) => degradada.
        integracoes.Estados.Add(new EstadoUltimaIntegracao("receita", Agora.AddHours(-2), null, null, 1, 0));

        var servico = Operacoes(new AutorizacaoPlataformaFake(), workers, integracoes);

        var painel = await servico.ObterPainelAsync();

        painel.Workers.Should().HaveCount(2);
        painel.WorkersEstagnados.Should().Be(1);
        painel.Workers.Single(w => w.Worker == "outbox").Estagnado.Should().BeTrue();
        painel.Workers.Single(w => w.Worker == "transmissao").Estagnado.Should().BeFalse();
        painel.IntegracoesDegradadas.Should().Be(1);
        painel.GeradoEmUtc.Should().Be(Agora);
    }

    [Fact]
    public async Task Operacoes_Mostra_A_Saude_Gravada_Pelo_Host_Do_Worker_E_Ela_Prevalece()
    {
        // A Web não roda os trabalhadores: o que vale para "outbox" é o que o host do Worker gravou no catálogo.
        var locais = new RegistroSaudeWorkersFake();
        locais.Estados.Add(new EstadoSaudeWorker("outbox", false, null, null, null, 0, null, null, 0, 0));
        var persistida = new RepositorioSaudeWorkersPersistidaFake();
        persistida.Estados.Add(new EstadoSaudeWorker("outbox", true, Agora.AddHours(-1), Agora.AddSeconds(-20), Agora.AddSeconds(-20), 7, null, null, 0, 0));
        // Host do Worker parado há 30 min: a última batida gravada envelhece ⇒ estagnado (não "vivo" por suposição).
        persistida.Estados.Add(new EstadoSaudeWorker("heartbeat", true, Agora.AddHours(-2), Agora.AddMinutes(-30), null, 0, null, null, 0, 0));

        var painel = await Operacoes(new AutorizacaoPlataformaFake(), locais, new RegistroUltimaIntegracaoFake(), persistida).ObterPainelAsync();

        painel.Workers.Should().HaveCount(2);
        var outbox = painel.Workers.Single(w => w.Worker == "outbox");
        outbox.Ativo.Should().BeTrue();
        outbox.TotalProcessado.Should().Be(7);
        painel.Workers.Single(w => w.Worker == "heartbeat").Estagnado.Should().BeTrue();
        painel.WorkersEstagnados.Should().Be(1);
    }
}
