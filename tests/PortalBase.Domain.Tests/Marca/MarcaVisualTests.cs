using System.Text.RegularExpressions;
using FluentAssertions;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Marca;

namespace PortalBase.Domain.Tests.Marca;

/// <summary>
/// Invariantes da logo configurável (<see cref="MarcaVisual"/>): só formatos raster seguros (PNG/JPEG),
/// tamanho limitado, hash de integridade calculado. Recusa SVG (XSS), vazio e excesso de tamanho.
/// </summary>
public sealed class MarcaVisualTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] ConteudoValido = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("IMAGE/PNG")]
    public void Criar_ComFormatoRasterValido_CalculaHash(string contentType)
    {
        var marca = MarcaVisual.Criar(ConteudoValido, contentType, Momento, atualizadoPor: Guid.NewGuid());

        marca.Id.Should().Be(MarcaVisual.IdUnico);
        marca.ContentType.Should().Be(contentType.ToLowerInvariant());
        marca.TamanhoBytes.Should().Be(ConteudoValido.Length);
        Regex.IsMatch(marca.HashSha256, "^[0-9a-f]{64}$").Should().BeTrue();
    }

    [Theory]
    [InlineData("image/svg+xml")] // SVG é vetor de XSS quando servido — recusado
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    public void Criar_ComFormatoNaoSuportado_Lanca(string contentType)
    {
        var acao = () => MarcaVisual.Criar(ConteudoValido, contentType, Momento, null);
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Criar_ComConteudoVazio_Lanca()
    {
        var acao = () => MarcaVisual.Criar([], "image/png", Momento, null);
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Criar_AcimaDoTamanhoMaximo_Lanca()
    {
        var grande = new byte[MarcaVisual.TamanhoMaximoBytes + 1];
        var acao = () => MarcaVisual.Criar(grande, "image/png", Momento, null);
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Aceita_Logo_De_Ate_10_MB()
    {
        MarcaVisual.TamanhoMaximoBytes.Should().Be(10L * 1024 * 1024);
        MarcaVisual.TamanhoMaximoTexto.Should().Be("10 MB");

        var noLimite = new byte[MarcaVisual.TamanhoMaximoBytes];
        ConteudoValido.CopyTo(noLimite, 0);
        MarcaVisual.Criar(noLimite, "image/png", Momento, null).TamanhoBytes.Should().Be(MarcaVisual.TamanhoMaximoBytes);
    }

    [Fact]
    public void Atualizar_SubstituiConteudo_E_Rehash()
    {
        var marca = MarcaVisual.Criar(ConteudoValido, "image/png", Momento, null);
        var hashAnterior = marca.HashSha256;

        var novo = new byte[] { 0xFF, 0xD8, 0xFF, 9, 9, 9 };
        marca.Atualizar(novo, "image/jpeg", Momento.AddMinutes(1), Guid.NewGuid());

        marca.ContentType.Should().Be("image/jpeg");
        marca.TamanhoBytes.Should().Be(novo.Length);
        marca.HashSha256.Should().NotBe(hashAnterior);
    }
}
