using FluentAssertions;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Notificacoes;

namespace PortalBase.Domain.Tests.Notificacoes;

public sealed class ConfiguracaoEmailTests
{
    private static readonly DateTimeOffset Momento = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ligada_Com_Todos_Os_Campos_E_Valida_E_Normaliza()
    {
        var c = ConfiguracaoEmail.Criar(true, " smtp.exemplo.com ", 587, null, " portal ", "nao-responda@exemplo.com", "", "https://portal.exemplo.com/", Momento, null);

        c.Id.Should().Be(ConfiguracaoEmail.IdUnico);
        c.Servidor.Should().Be("smtp.exemplo.com");
        c.Seguranca.Should().Be(ConfiguracaoEmail.SegurancaStartTls, "o padrão é STARTTLS");
        c.Usuario.Should().Be("portal");
        c.RemetenteNome.Should().Be("Portal Base");
        c.UrlPublicaPortal.Should().Be("https://portal.exemplo.com", "sem a barra final");
    }

    [Theory]
    [InlineData("", 587, "r@e.com", "https://p")]
    [InlineData("smtp", 0, "r@e.com", "https://p")]
    [InlineData("smtp", 70000, "r@e.com", "https://p")]
    [InlineData("smtp", 587, "", "https://p")]
    [InlineData("smtp", 587, "r@e.com", "")]
    public void Ligada_Exige_Servidor_Porta_Remetente_E_Url(string servidor, int porta, string remetente, string url)
    {
        var acao = () => ConfiguracaoEmail.Criar(true, servidor, porta, "StartTls", "", remetente, "Portal", url, Momento, null);

        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Desligada_Aceita_Campos_Vazios()
    {
        var c = ConfiguracaoEmail.Criar(false, "", 0, "", "", "", "", "", Momento, null);

        c.Habilitado.Should().BeFalse();
        c.Porta.Should().Be(587);
    }

    [Fact]
    public void Seguranca_Desconhecida_E_Limites_Sao_Recusados()
    {
        var seguranca = () => ConfiguracaoEmail.Criar(true, "smtp", 587, "Talvez", "", "r@e.com", "P", "https://p", Momento, null);
        var longo = () => ConfiguracaoEmail.Criar(true, new string('s', ConfiguracaoEmail.TamanhoMaximoServidor + 1), 587, "StartTls", "", "r@e.com", "P", "https://p", Momento, null);

        seguranca.Should().Throw<EntradaInvalidaException>();
        longo.Should().Throw<EntradaInvalidaException>();
    }

    [Fact]
    public void Atualizar_Troca_O_Carimbo_De_Concorrencia()
    {
        var c = ConfiguracaoEmail.Criar(false, "", 0, "", "", "", "", "", Momento, null);
        var carimbo = c.ConcurrencyStamp;
        var autor = Guid.NewGuid();

        c.Atualizar(true, "smtp", 465, "SslTls", "", "r@e.com", "P", "https://p", Momento.AddHours(1), autor);

        c.ConcurrencyStamp.Should().NotBe(carimbo);
        c.AtualizadoPorUsuarioId.Should().Be(autor);
        c.Seguranca.Should().Be(ConfiguracaoEmail.SegurancaSslTls);
    }
}
