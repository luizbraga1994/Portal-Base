using FluentAssertions;
using PortalBase.Application.Auditoria;

namespace PortalBase.Application.Tests.Auditoria;

/// <summary>Política de retenção por categoria (seção 18): prazos e janelas.</summary>
public sealed class RetencaoTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
    private readonly PoliticaRetencao _politica = new();

    [Theory]
    [InlineData(CategoriaRetencao.Auditoria, 10)]
    [InlineData(CategoriaRetencao.ArquivosImportacao, 5)]
    public void Prazos_Conforme_Documentacao(CategoriaRetencao categoria, int anos)
        => _politica.AnosDe(categoria).Should().Be(anos);

    [Fact]
    public void ExpiraEm_Soma_Os_Anos_Da_Categoria()
        => _politica.ExpiraEm(CategoriaRetencao.Auditoria, Base).Should().Be(Base.AddYears(10));

    [Fact]
    public void Registro_Dentro_Da_Janela_Permanece()
    {
        // Auditoria: 10 anos. 9 anos depois ainda está dentro da janela.
        _politica.DentroDaJanela(CategoriaRetencao.Auditoria, Base, Base.AddYears(9)).Should().BeTrue();
    }

    [Fact]
    public void Registro_Alem_Da_Janela_E_Identificado()
    {
        // Arquivos de importação: 5 anos. 6 anos depois está fora da janela obrigatória.
        _politica.DentroDaJanela(CategoriaRetencao.ArquivosImportacao, Base, Base.AddYears(6)).Should().BeFalse();
    }
}
