using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PortalBase.Application.Marca;
using PortalBase.Web.Seguranca;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// UI-005 (auditoria de 08/10/2026), teste permanente: com o navegador já tendo a MESMA logo (If-None-Match igual ao
/// hash), o endpoint responde 304 a partir do RESUMO — sem ler a imagem (até 10 MB) do banco. Sem cabeçalho, outra
/// versão ou sem logo, segue para servir (ou 404).
/// </summary>
public sealed class MarcaNaoModificadaTests
{
    private static readonly ResumoMarcaVisual Resumo = new("image/png", "abc123", 2_000_000);

    private static DefaultHttpContext Contexto(string? ifNoneMatch)
    {
        var contexto = new DefaultHttpContext();
        if (ifNoneMatch is not null)
        {
            contexto.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        return contexto;
    }

    [Theory]
    [InlineData("\"abc123\"")]
    [InlineData("W/\"abc123\"")]
    [InlineData("\"outra\", \"abc123\"")]
    [InlineData("*")]
    public void Mesma_Logo_No_Navegador_Responde_304_Sem_Ler_A_Imagem(string ifNoneMatch)
    {
        var contexto = Contexto(ifNoneMatch);

        var resultado = EndpointsMarca.NaoModificada(contexto, Resumo, publico: true);

        resultado.Should().NotBeNull();
        resultado.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status304NotModified);
        contexto.Response.Headers.ETag.ToString().Should().Be("\"abc123\"");
        contexto.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=300");
    }

    [Fact]
    public void Logo_Do_Contribuinte_No_304_Continua_Privada()
    {
        var contexto = Contexto("\"abc123\"");

        EndpointsMarca.NaoModificada(contexto, Resumo, publico: false).Should().NotBeNull();

        contexto.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=60");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("\"versao-antiga\"")]
    public void Sem_Cabecalho_Ou_Outra_Versao_Segue_Para_Servir(string? ifNoneMatch)
        => EndpointsMarca.NaoModificada(Contexto(ifNoneMatch), Resumo, publico: true).Should().BeNull();

    [Fact]
    public void Sem_Logo_Segue_Para_O_404()
        => EndpointsMarca.NaoModificada(Contexto("*"), null, publico: true).Should().BeNull();
}
