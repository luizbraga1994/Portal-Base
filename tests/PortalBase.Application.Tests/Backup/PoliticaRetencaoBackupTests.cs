using FluentAssertions;
using PortalBase.Application.Backup;
using PortalBase.Domain.Backup;

namespace PortalBase.Application.Tests.Backup;

/// <summary>
/// Prompt 50 — política de RETENÇÃO (Fase 9): janelas dirigidas por configuração; nunca descarta dentro
/// da janela; e a garantia inegociável — NUNCA apaga o único backup conhecido de um alvo (mesmo expirado).
/// </summary>
public sealed class PoliticaRetencaoBackupTests
{
    private const string HashValido = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Agora = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static PoliticaRetencaoBackup Politica(OpcoesBackup? opcoes = null)
        => new(opcoes ?? new OpcoesBackup { RetencaoDiariaDias = 7, RetencaoSemanalDias = 35, RetencaoMensalDias = 365 });

    [Theory]
    [InlineData(PeriodicidadeBackup.Diario, 7)]
    [InlineData(PeriodicidadeBackup.Semanal, 35)]
    [InlineData(PeriodicidadeBackup.Mensal, 365)]
    public void CalcularRetencao_Usa_Config_Por_Periodicidade(PeriodicidadeBackup periodicidade, int dias)
        => Politica().CalcularRetencaoAte(periodicidade, Agora).Should().Be(Agora.AddDays(dias));

    [Fact] // dentro da janela ⇒ nunca descarta
    public void PodeDescartar_Dentro_Da_Janela_Falso()
    {
        var registro = Concluido(retencaoAteUtc: Agora.AddDays(5));
        PoliticaRetencaoBackup.PodeDescartar(registro, totalConcluidosDoAlvo: 3, Agora).Should().BeFalse();
    }

    [Fact] // expirado, porém é o ÚNICO backup do alvo ⇒ nunca descarta (última linha de defesa)
    public void PodeDescartar_Expirado_Unico_Falso()
    {
        var registro = Concluido(retencaoAteUtc: Agora.AddDays(-1));
        PoliticaRetencaoBackup.PodeDescartar(registro, totalConcluidosDoAlvo: 1, Agora).Should().BeFalse();
    }

    [Fact] // expirado e há mais de um backup do alvo ⇒ pode descartar
    public void PodeDescartar_Expirado_Com_Redundancia_Verdadeiro()
    {
        var registro = Concluido(retencaoAteUtc: Agora.AddDays(-1));
        PoliticaRetencaoBackup.PodeDescartar(registro, totalConcluidosDoAlvo: 2, Agora).Should().BeTrue();
    }

    private static RegistroBackup Concluido(DateTimeOffset retencaoAteUtc)
    {
        var r = RegistroBackup.Iniciar(TipoBackup.TenantCompleto, Guid.NewGuid(), "tenant_x", Agora.AddDays(-2));
        r.Concluir(HashValido, 1024, "v", "primario/x.bak", criptografado: false, chaveRef: null, imutavel: false, retencaoAteUtc, Agora.AddDays(-2));
        return r;
    }
}
