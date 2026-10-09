using FluentAssertions;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Incidentes;

namespace PortalBase.Domain.Tests.Incidentes;

/// <summary>
/// Registro de INCIDENTE DE SEGURANÇA (Prompt 30, seções 45-47) — ciclo detecção → análise → contenção →
/// classificação (decisão HUMANA de risco ao titular — §46) → evidências (§47) → encerramento; timeline
/// APPEND-ONLY; incidente encerrado é IMUTÁVEL. Estados impossíveis são impedidos pelo domínio.
/// </summary>
public sealed class IncidenteSegurancaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static IncidenteSeguranca Novo(TipoIncidente tipo = TipoIncidente.Tecnico)
        => IncidenteSeguranca.Registrar(null, Guid.NewGuid(), tipo, SeveridadeIncidente.Alto,
            "Acesso anômalo detectado", "Tentativas repetidas de acesso — investigar.", "Web", "Autenticacao", "op-1", "corr-1", T0);

    [Fact]
    public void Registrar_ComeçaDetectado_ComTimeline()
    {
        var inc = Novo();
        inc.Status.Should().Be(StatusIncidente.Detectado);
        inc.Tipo.Should().Be(TipoIncidente.Tecnico);
        inc.ClassificacaoRisco.Should().Be(ClassificacaoRiscoTitular.NaoAvaliado); // fail-closed
        inc.Codigo.Should().StartWith("INC-");
        inc.Timeline.Should().ContainSingle().Which.Tipo.Should().Be(TipoEntradaTimeline.Deteccao);
    }

    [Fact] // §75: ciclo completo técnico → dados pessoais → evidência → encerramento.
    public void CicloCompleto_TecnicoParaDadosPessoais()
    {
        var inc = Novo();
        inc.IniciarAnalise("op-1", "analisando logs", T0.AddMinutes(5));
        inc.Status.Should().Be(StatusIncidente.EmAnalise);

        inc.RegistrarContencao("op-1", "sessões revogadas; IP bloqueado", T0.AddMinutes(10));
        inc.Status.Should().Be(StatusIncidente.Contido);

        inc.ClassificarRiscoTitular("dpo-1", envolveDadosPessoais: true, ClassificacaoRiscoTitular.ComRiscoOuDanoRelevante, "dados de contato expostos", "dpo-1", T0.AddMinutes(15));
        inc.Tipo.Should().Be(TipoIncidente.ComDadosPessoais);
        inc.EnvolveDadosPessoais.Should().BeTrue();
        inc.ClassificacaoRisco.Should().Be(ClassificacaoRiscoTitular.ComRiscoOuDanoRelevante);
        inc.JustificativaClassificacao.Should().NotBeNullOrEmpty();
        inc.ResponsavelId.Should().Be("dpo-1");

        inc.AssociarEvidencias("op-1", "abc123hash", "evidencias/inc-1.zip", T0.AddMinutes(20));
        inc.HashEvidencias.Should().Be("abc123hash");
        inc.ReferenciaEvidencias.Should().Be("evidencias/inc-1.zip");

        inc.Encerrar("dpo-1", "contido e comunicado internamente", T0.AddMinutes(30));
        inc.Status.Should().Be(StatusIncidente.Encerrado);
        inc.EstaEncerrado.Should().BeTrue();

        // Timeline append-only, sequência crescente, nada removido.
        inc.Timeline.Select(t => t.Sequencia).Should().BeInAscendingOrder();
        inc.Timeline.Should().HaveCount(6); // detecção + análise + contenção + classificação + evidência + encerramento
    }

    [Fact] // §46: classificação exige decisão explícita — 'NaoAvaliado' é rejeitado (nunca presume ausência de risco).
    public void Classificar_ExigeDecisaoExplicita()
    {
        var inc = Novo();
        inc.IniciarAnalise("op-1", null, T0.AddMinutes(1));
        var acao = () => inc.ClassificarRiscoTitular("dpo-1", true, ClassificacaoRiscoTitular.NaoAvaliado, "x", "dpo-1", T0.AddMinutes(2));
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact] // §46: a classificação exige justificativa (accountability).
    public void Classificar_ExigeJustificativa()
    {
        var inc = Novo();
        var acao = () => inc.ClassificarRiscoTitular("dpo-1", true, ClassificacaoRiscoTitular.SemRiscoRelevante, "  ", "dpo-1", T0.AddMinutes(2));
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact] // §45: não encerra direto da detecção (exige análise/contenção).
    public void Encerrar_DiretoDaDeteccao_Falha()
    {
        var inc = Novo();
        var acao = () => inc.Encerrar("op-1", "resumo", T0.AddMinutes(1));
        acao.Should().Throw<EstadoInvalidoException>();
    }

    [Fact] // §45/§47: incidente encerrado é IMUTÁVEL (append-only preservado).
    public void Encerrado_EhImutavel()
    {
        var inc = Novo();
        inc.IniciarAnalise("op-1", null, T0.AddMinutes(1));
        inc.Encerrar("op-1", "resolvido", T0.AddMinutes(2));
        var acao = () => inc.AdicionarNota("op-1", "nota tardia", T0.AddMinutes(3));
        acao.Should().Throw<EstadoInvalidoException>();
    }
}
