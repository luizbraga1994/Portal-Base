using FluentAssertions;
using PortalBase.Application.Consultas;

namespace PortalBase.Application.Tests.Consultas;

/// <summary>Contrato do <see cref="ResultadoPaginado{T}"/> e dos limites de <see cref="Paginacao"/> (Prompt 36, §3-4).</summary>
public sealed class ResultadoPaginadoTests
{
    private static readonly int[] DoisItens = [1, 2];
    private static readonly int[] DoisUltimos = [9, 10];

    [Theory] // total/tamanho → total de páginas (arredonda para cima)
    [InlineData(0, 50, 0)]
    [InlineData(1, 50, 1)]
    [InlineData(50, 50, 1)]
    [InlineData(51, 50, 2)]
    [InlineData(10, 3, 4)]
    public void TotalPaginas_Arredonda_Para_Cima(long total, int tamanho, int esperado)
    {
        var pagina = new ResultadoPaginado<int>(Array.Empty<int>(), 1, tamanho, total);
        pagina.TotalPaginas.Should().Be(esperado);
    }

    [Fact] // navegação: primeira página não tem anterior; havendo mais páginas, tem próxima
    public void Navegacao_Anterior_Proxima()
    {
        var primeira = new ResultadoPaginado<int>(DoisItens, 1, 2, 10);
        primeira.TemAnterior.Should().BeFalse();
        primeira.TemProxima.Should().BeTrue();

        var ultima = new ResultadoPaginado<int>(DoisUltimos, 5, 2, 10);
        ultima.TemAnterior.Should().BeTrue();
        ultima.TemProxima.Should().BeFalse();
    }

    [Fact] // página vazia é consistente (sem anterior/próxima)
    public void Vazio_Nao_Tem_Navegacao()
    {
        var vazio = ResultadoPaginado.Vazio<string>(1, 50);
        vazio.Itens.Should().BeEmpty();
        vazio.TotalItens.Should().Be(0);
        vazio.TotalPaginas.Should().Be(0);
        vazio.TemAnterior.Should().BeFalse();
        vazio.TemProxima.Should().BeFalse();
    }

    [Theory] // construção inválida falha (fail-fast)
    [InlineData(0, 50, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 50, -1)]
    public void Construcao_Invalida_Lanca(int pagina, int tamanho, long total)
    {
        var acao = () => new ResultadoPaginado<int>(Array.Empty<int>(), pagina, tamanho, total);
        acao.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory] // tamanho de página normalizado à faixa segura (nunca pageSize=1000000)
    [InlineData(0, Paginacao.TamanhoPadrao)]
    [InlineData(-5, Paginacao.TamanhoPadrao)]
    [InlineData(1, 1)]
    [InlineData(200, 200)]
    [InlineData(1_000_000, Paginacao.TamanhoMaximo)]
    public void NormalizarTamanho_Respeita_Limites(int solicitado, int esperado)
        => Paginacao.NormalizarTamanho(solicitado).Should().Be(esperado);

    [Theory] // página normalizada à base 1
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(7, 7)]
    public void NormalizarPagina_Base_1(int solicitada, int esperada)
        => Paginacao.NormalizarPagina(solicitada).Should().Be(esperada);
}
