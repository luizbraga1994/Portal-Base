using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Observabilidade;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Application.Tests.Observabilidade;

/// <summary>
/// Ponte ALERTA → INCIDENTE (Prompt 31, seções 50, 41) — alerta acima do limiar origina um incidente TÉCNICO
/// (nunca ComDadosPessoais automaticamente — §50), sempre auditado; alertas recorrentes deduplicam (uma nota,
/// não vários incidentes — §41). Nunca payload/PII (§3).
/// </summary>
public sealed class ServicoPonteAlertaIncidenteTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly RepositorioIncidentesFake _repo = new();
    private readonly AuditoriaFake _auditoria = new();
    private readonly OpcoesObservabilidade _opcoes = new() { SeveridadeMinimaIncidente = SeveridadeAlerta.Alto };

    private ServicoPonteAlertaIncidente Servico() => new(_repo, _auditoria, new RelogioFixo(T0), _opcoes);

    private static AlertaOperacional Alerta(SeveridadeAlerta sev, string codigo = "worker_stalled")
        => new(codigo, sev, "worker:transmissao-dere", "Worker possivelmente estagnado.", null, "corr-1");

    [Fact] // §50: abaixo do limiar ⇒ NÃO cria incidente, mas AUDITA o alerta.
    public async Task AbaixoDoLimiar_NaoCriaIncidente_MasAudita()
    {
        var inc = await Servico().ProcessarAlertaAsync(Alerta(SeveridadeAlerta.Warning), CancellationToken.None);
        inc.Should().BeNull();
        _repo.Itens.Should().BeEmpty();
        _auditoria.Eventos.Exists(e => e.Operacao == AcaoAuditoria.AlertaOperacionalDisparado).Should().BeTrue();
    }

    [Fact] // §50: acima do limiar ⇒ incidente TÉCNICO (nunca com dados pessoais automaticamente).
    public async Task AcimaDoLimiar_CriaIncidenteTecnico()
    {
        var inc = await Servico().ProcessarAlertaAsync(Alerta(SeveridadeAlerta.Critico), CancellationToken.None);
        inc.Should().NotBeNull();
        inc!.Tipo.Should().Be(TipoIncidente.Tecnico);
        inc.EnvolveDadosPessoais.Should().BeFalse();
        inc.Severidade.Should().Be(SeveridadeIncidente.Critico);
        _repo.Itens.Should().ContainSingle();
        _auditoria.Eventos.Exists(e => e.Operacao == AcaoAuditoria.IncidenteRegistrado).Should().BeTrue();
    }

    [Fact] // §41: alertas recorrentes do mesmo código deduplicam (um incidente + nota).
    public async Task AlertaRecorrente_Deduplica()
    {
        var servico = Servico();
        var primeiro = await servico.ProcessarAlertaAsync(Alerta(SeveridadeAlerta.Alto), CancellationToken.None);
        var segundo = await servico.ProcessarAlertaAsync(Alerta(SeveridadeAlerta.Alto), CancellationToken.None);

        _repo.Itens.Should().ContainSingle(); // não duplicou o incidente
        segundo!.Id.Should().Be(primeiro!.Id);
        primeiro.Timeline.Count(t => t.Tipo == TipoEntradaTimeline.Nota).Should().Be(1); // nota do alerta recorrente
    }
}
