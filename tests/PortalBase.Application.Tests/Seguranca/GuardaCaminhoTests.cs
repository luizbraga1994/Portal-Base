using FluentAssertions;
using PortalBase.Application.Seguranca;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Tests.Seguranca;

/// <summary>Proteção contra path traversal e nomes de arquivo perigosos (Prompt 07, seção 15).</summary>
public sealed class GuardaCaminhoTests
{
    [Theory]
    [InlineData("relatorio.xml")]
    [InlineData("balancete-2026-01.csv")]
    public void Nomes_Simples_Sao_Validos(string nome)
        => GuardaCaminho.NomeValido(nome).Should().BeTrue();

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\system32")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\segredo.txt")]
    [InlineData("\\\\servidor\\share\\x")]
    [InlineData("pasta/arquivo.xml")]
    [InlineData("..")]
    [InlineData("")]
    public void Nomes_Perigosos_Sao_Rejeitados(string nome)
        => GuardaCaminho.NomeValido(nome).Should().BeFalse();

    [Fact]
    public void Nome_Com_Caractere_De_Controle_E_Rejeitado()
        => GuardaCaminho.NomeValido("arq\u0000.xml").Should().BeFalse();

    [Fact]
    public void ValidarNome_Lanca_Para_Traversal()
    {
        var acao = () => GuardaCaminho.ValidarNome("../x");
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void GerarNome_Produz_Nome_Seguro_Com_Extensao()
    {
        var nome = GuardaCaminho.GerarNome("XML");
        nome.Should().EndWith(".xml");
        GuardaCaminho.NomeValido(nome).Should().BeTrue();
    }

    [Fact]
    public void CombinarDentroDe_Mantem_Sob_A_Raiz()
    {
        var combinado = GuardaCaminho.CombinarDentroDe("/dados/uploads", "ok.xml");
        combinado.Should().StartWith(Path.GetFullPath("/dados/uploads"));
    }

    [Fact]
    public void CombinarDentroDe_Rejeita_Nome_Invalido()
    {
        var acao = () => GuardaCaminho.CombinarDentroDe("/dados/uploads", "../fora.xml");
        acao.Should().Throw<EntradaInvalidaException>();
    }
}
