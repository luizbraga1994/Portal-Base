using FluentAssertions;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Notificacoes;

namespace PortalBase.Domain.Tests.Notificacoes;

/// <summary>Aparência dos e-mails: tema conhecido (padrão = layout do portal), cor #RRGGBB opcional e rodapé só texto.</summary>
public sealed class AparenciaEmailTests
{
    private static readonly DateTimeOffset Momento = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static ConfiguracaoEmail Nova() => ConfiguracaoEmail.Criar(false, "", 0, "", "", "", "", "", Momento, null);

    [Fact]
    public void Nova_Configuracao_Usa_O_Tema_Do_Portal()
    {
        var c = Nova();

        c.Tema.Should().Be(TemasEmail.Padrao).And.Be("Portal");
        c.CorDestaque.Should().BeEmpty();
        c.TextoRodape.Should().BeEmpty();
    }

    [Fact]
    public void Define_Tema_Cor_E_Rodape()
    {
        var c = Nova();
        var stampAntes = c.ConcurrencyStamp;
        var autor = Guid.NewGuid();

        c.DefinirAparencia(" Carmim ", "#c0142a", " Andersen Consulting\r\n(11) 0000-0000 ", Momento.AddHours(1), autor);

        c.Tema.Should().Be("Carmim");
        c.CorDestaque.Should().Be("#C0142A");
        c.TextoRodape.Should().Be("Andersen Consulting\n(11) 0000-0000");
        c.AtualizadoPorUsuarioId.Should().Be(autor);
        c.ConcurrencyStamp.Should().NotBe(stampAntes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Tema_Vazio_Volta_Ao_Padrao(string? tema)
    {
        var c = Nova();

        c.DefinirAparencia(tema, null, null, Momento, null);

        c.Tema.Should().Be(TemasEmail.Padrao);
    }

    [Fact]
    public void Todos_Os_Temas_Da_Lista_Sao_Aceitos()
    {
        foreach (var tema in TemasEmail.Todos)
        {
            var c = Nova();
            c.DefinirAparencia(tema.Codigo, null, null, Momento, null);
            c.Tema.Should().Be(tema.Codigo);
        }

        TemasEmail.Todos.Select(t => t.Codigo).Should().OnlyHaveUniqueItems().And.Contain(["Portal", "PortalEscuro", "Carmim", "Azul", "Neutro"]);
    }

    [Theory]
    [InlineData("Desconhecido", null, null, "Tema")]
    [InlineData("Portal", "vermelho", null, "#RRGGBB")]
    [InlineData("Portal", "#12345", null, "#RRGGBB")]
    [InlineData("Portal", "#GGGGGG", null, "#RRGGBB")]
    [InlineData("Portal", null, "linha\u0007bip", "inválidos")]
    public void Invalida_Recusa_E_Nao_Muda_Nada(string tema, string? cor, string? rodape, string trecho)
    {
        var c = Nova();

        var acao = () => c.DefinirAparencia(tema, cor, rodape, Momento, null);

        acao.Should().Throw<EntradaInvalidaException>().Which.Message.Should().Contain(trecho);
        c.Tema.Should().Be(TemasEmail.Padrao);
        c.CorDestaque.Should().BeEmpty();
    }

    [Fact]
    public void Rodape_Acima_Do_Limite_Recusa()
    {
        var acao = () => Nova().DefinirAparencia("Portal", null, new string('x', ConfiguracaoEmail.TamanhoMaximoTextoRodape + 1), Momento, null);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Tema_Desconhecido_Na_Montagem_Cai_No_Padrao()
    {
        TemasEmail.Obter("Inexistente").Codigo.Should().Be(TemasEmail.Padrao);
        TemasEmail.Obter(null).Codigo.Should().Be(TemasEmail.Padrao);
        TemasEmail.Obter("Azul").Nome.Should().Be("Azul corporativo");
    }
}
