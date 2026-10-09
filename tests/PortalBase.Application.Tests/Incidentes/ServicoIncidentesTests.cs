using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Incidentes;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Application.Tests.Incidentes;

/// <summary>
/// Governança de INCIDENTES (Prompt 30, seções 45-47, 75) — autorização SERVER-SIDE de plataforma (deny-by-default),
/// ciclo técnico → dados pessoais → evidência → encerramento, auditoria de cada passo SEM PII/payload (§22/§41).
/// </summary>
public sealed class ServicoIncidentesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly AutorizacaoPlataformaFake _autorizacao = new() { Permitir = true };
    private readonly ContextoSegurancaFake _seguranca = new() { PrincipalId = "dpo-1", EstaAutenticado = true, MfaSatisfeito = true };
    private readonly RepositorioIncidentesFake _repo = new();
    private readonly AuditoriaFake _auditoria = new();

    private ServicoIncidentes Servico() => new(_autorizacao, _seguranca, _repo, _auditoria, new RelogioFixo(T0));

    private static NovoIncidente Dados() => new(
        Guid.NewGuid(), TipoIncidente.Tecnico, SeveridadeIncidente.Alto, "Acesso anômalo", "Investigar tentativas repetidas.", "Web", "Autenticacao");

    [Fact] // §30/§8: sem autorização de plataforma ⇒ falha (deny-by-default; UI nunca é suficiente).
    public async Task SemAutorizacao_Falha()
    {
        _autorizacao.Permitir = false;
        var acao = async () => await Servico().RegistrarAsync(Dados(), CancellationToken.None);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        _repo.Itens.Should().BeEmpty();
    }

    [Fact] // §75: ciclo completo — registrar → analisar → conter → classificar (dados pessoais) → evidência → encerrar.
    public async Task CicloCompleto_RegistraEAudita()
    {
        var servico = Servico();
        var inc = await servico.RegistrarAsync(Dados(), CancellationToken.None);
        await servico.IniciarAnaliseAsync(inc.Id, "análise iniciada", CancellationToken.None);
        await servico.RegistrarContencaoAsync(inc.Id, "sessões revogadas", CancellationToken.None);
        await servico.ClassificarRiscoTitularAsync(inc.Id, envolveDadosPessoais: true, ClassificacaoRiscoTitular.ComRiscoOuDanoRelevante, "contato exposto", "dpo-1", CancellationToken.None);
        await servico.AssociarEvidenciasAsync(inc.Id, "hash123", "evidencias/inc.zip", CancellationToken.None);
        await servico.EncerrarAsync(inc.Id, "contido e registrado", CancellationToken.None);

        var final = await _repo.ObterPorIdAsync(inc.Id, CancellationToken.None);
        final!.Status.Should().Be(StatusIncidente.Encerrado);
        final.Tipo.Should().Be(TipoIncidente.ComDadosPessoais);

        // §22: cada passo auditado; nenhum metadado contém payload/PII (só código/tipo/severidade/status).
        TemEvento(AcaoAuditoria.IncidenteRegistrado).Should().BeTrue();
        TemEvento(AcaoAuditoria.IncidenteClassificado).Should().BeTrue();
        TemEvento(AcaoAuditoria.IncidenteEncerrado).Should().BeTrue();
        _auditoria.Eventos.TrueForAll(e => e.Metadados is null || e.Metadados.Keys.All(k => k is "incidente" or "tipo" or "severidade" or "status" or "tenantAfetado")).Should().BeTrue();
    }

    [Fact] // §46: a classificação de risco é decisão humana registrada (justificativa + responsável).
    public async Task Classificacao_RegistraDecisaoHumana()
    {
        var servico = Servico();
        var inc = await servico.RegistrarAsync(Dados(), CancellationToken.None);
        await servico.ClassificarRiscoTitularAsync(inc.Id, true, ClassificacaoRiscoTitular.SemRiscoRelevante, "sem exposição efetiva", "dpo-1", CancellationToken.None);
        var final = await _repo.ObterPorIdAsync(inc.Id, CancellationToken.None);
        final!.ClassificacaoRisco.Should().Be(ClassificacaoRiscoTitular.SemRiscoRelevante);
        final.JustificativaClassificacao.Should().NotBeNullOrEmpty();
        final.ResponsavelId.Should().Be("dpo-1");
    }

    private bool TemEvento(string acao) => _auditoria.Eventos.Exists(e => e.Operacao == acao);
}
