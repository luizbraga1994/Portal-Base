using FluentAssertions;
using PortalBase.Application.Auditoria;

namespace PortalBase.Application.Tests.Auditoria;

/// <summary>
/// Prompt 57 (Fase 15/40) — a classificação LGPD de eliminabilidade nunca deixa expurgar dado de
/// retenção legal obrigatória (fiscal/auditoria), mesmo após a janela; e só libera expurgo de dado
/// elegível DEPOIS da janela. É guarda puro (não apaga nada).
/// </summary>
public sealed class ClassificacaoLgpdTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly PoliticaRetencao _politica = new();

    [Theory]
    [InlineData(CategoriaRetencao.Auditoria)]
    [InlineData(CategoriaRetencao.ArquivosImportacao)]
    public void Toda_Categoria_Atual_E_Retencao_Legal_Obrigatoria(CategoriaRetencao categoria)
        => ClassificacaoLgpd.Classe(categoria).Should().Be(ClasseLgpdRetencao.RetencaoLegalObrigatoria);

    [Theory]
    [InlineData(CategoriaRetencao.Auditoria)]
    [InlineData(CategoriaRetencao.ArquivosImportacao)]
    public void Dado_Fiscal_Obrigatorio_Nunca_E_Expurgavel_Mesmo_Apos_A_Janela(CategoriaRetencao categoria)
    {
        // 50 anos depois — muito além de qualquer janela — ainda assim NÃO pode ser expurgado.
        var muitoDepois = Base.AddYears(50);
        ClassificacaoLgpd.PodeSerExpurgado(_politica, categoria, Base, muitoDepois).Should().BeFalse();
    }

    [Fact]
    public void Categoria_Desconhecida_E_Conservadora_Preservar()
    {
        var desconhecida = (CategoriaRetencao)999;
        ClassificacaoLgpd.Classe(desconhecida).Should().Be(ClasseLgpdRetencao.RetencaoLegalObrigatoria);
        ClassificacaoLgpd.PodeSerExpurgado(_politica, desconhecida, Base, Base.AddYears(50)).Should().BeFalse();
    }

    [Theory]
    [InlineData(ClasseLgpdRetencao.RetencaoLegalObrigatoria, true, false)]
    [InlineData(ClasseLgpdRetencao.RetencaoLegalObrigatoria, false, false)]
    [InlineData(ClasseLgpdRetencao.ElegivelExpurgoAposJanela, true, false)]   // dentro da janela: não
    [InlineData(ClasseLgpdRetencao.ElegivelExpurgoAposJanela, false, true)]   // fora da janela: sim
    public void Nucleo_Puro_So_Libera_Elegivel_Fora_Da_Janela(ClasseLgpdRetencao classe, bool dentroDaJanela, bool esperado)
        => ClassificacaoLgpd.PodeSerExpurgado(classe, dentroDaJanela).Should().Be(esperado);
}
