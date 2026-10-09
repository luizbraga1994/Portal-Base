using FluentAssertions;
using PortalBase.Infrastructure.Observabilidade;
using Serilog.Events;
using Serilog.Parsing;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>Regra 8: parâmetros sensíveis em URLs (ex.: o link de redefinição de senha) saem mascarados dos logs.</summary>
public sealed class RedacaoLogsTests
{
    [Theory]
    [InlineData("?token=WMasZCDYwcjOICb03tdLAWVnOfxR9XwDB0_P2HUQReM", "?token=***")]
    [InlineData("/conta/redefinir-senha?token=abc&erro=politica", "/conta/redefinir-senha?token=***&erro=politica")]
    [InlineData("?returnUrl=%2F&Token=abc", "?returnUrl=%2F&Token=***")]
    [InlineData("?codigo=123456", "?codigo=***")]
    [InlineData("senha=Segredo#1 e mais", "senha=*** e mais")]
    public void Valores_Sensiveis_Sao_Mascarados(string original, string esperado)
        => EnriquecedorRedacaoLogs.Mascarar(original).Should().Be(esperado);

    [Theory]
    [InlineData("/conta/entrar?erro=1")]
    [InlineData("/dados/tokenizacao?x=1")] // "token" fora de nome de parâmetro não é mexido
    public void Texto_Sem_Parametro_Sensivel_Fica_Igual(string original)
        => EnriquecedorRedacaoLogs.Mascarar(original).Should().Be(original);

    [Fact]
    public void Enriquecedor_Troca_A_Propriedade_Antes_Da_Saida()
    {
        var evento = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Request starting {Path}{QueryString}"),
            [new LogEventProperty("Path", new ScalarValue("/conta/redefinir-senha")), new LogEventProperty("QueryString", new ScalarValue("?token=segredo-de-uso-unico"))]);

        new EnriquecedorRedacaoLogs().Enrich(evento, new FabricaPropriedades());

        evento.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Be("Request starting \"/conta/redefinir-senha\"\"?token=***\"").And.NotContain("segredo-de-uso-unico");
    }

    private sealed class FabricaPropriedades : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
