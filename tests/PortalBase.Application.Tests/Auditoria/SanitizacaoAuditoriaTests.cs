using FluentAssertions;
using PortalBase.Application.Auditoria;

namespace PortalBase.Application.Tests.Auditoria;

/// <summary>Sanitização centralizada (seções 6, 7, 13): segredos jamais aparecem na trilha.</summary>
public sealed class SanitizacaoAuditoriaTests
{
    [Theory]
    [InlineData("senha")]
    [InlineData("Senha")]
    [InlineData("password")]
    [InlineData("hashSenha")]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("refresh_token")]
    [InlineData("segredoMfa")]
    [InlineData("totp")]
    [InlineData("recoveryCode")]
    [InlineData("clientSecret")]
    [InlineData("connectionString")]
    [InlineData("privateKey")]
    [InlineData("certificadoPfx")]
    [InlineData("Authorization")]
    public void Chaves_Sensiveis_Sao_Redigidas(string chave)
    {
        var entrada = new Dictionary<string, string?> { [chave] = "VALOR-SECRETO" };

        var saida = SanitizacaoAuditoria.Sanitizar(entrada);

        saida[chave].Should().Be(SanitizacaoAuditoria.Redigido);
        saida[chave].Should().NotContain("SECRETO");
    }

    [Fact]
    public void Chave_Nao_Sensivel_E_Preservada()
    {
        var entrada = new Dictionary<string, string?> { ["papelAnterior"] = "Leitor", ["papelNovo"] = "Analista" };

        var saida = SanitizacaoAuditoria.Sanitizar(entrada);

        saida["papelAnterior"].Should().Be("Leitor");
        saida["papelNovo"].Should().Be("Analista");
    }

    [Fact]
    public void Texto_Livre_Com_Padrao_Sensivel_E_Redigido()
    {
        var texto = "falha ao conectar password=SuperSecreta123 no host";

        var saida = SanitizacaoAuditoria.SanitizarTexto(texto);

        saida.Should().NotContain("SuperSecreta123");
        saida.Should().Contain(SanitizacaoAuditoria.Redigido);
    }

    [Fact]
    public void Valor_Longo_E_Truncado()
    {
        var entrada = new Dictionary<string, string?> { ["obs"] = new string('x', 5000) };

        var saida = SanitizacaoAuditoria.Sanitizar(entrada);

        saida["obs"]!.Length.Should().BeLessThanOrEqualTo(512);
    }

    [Fact]
    public void Dicionario_Nulo_Retorna_Vazio()
        => SanitizacaoAuditoria.Sanitizar(null).Should().BeEmpty();
}
