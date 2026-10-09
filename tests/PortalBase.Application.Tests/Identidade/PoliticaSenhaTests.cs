using FluentAssertions;
using PortalBase.Application.Identidade;

namespace PortalBase.Application.Tests.Identidade;

/// <summary>Política de senha única do portal (troca, redefinição e acesso local).</summary>
public sealed class PoliticaSenhaTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Curta#1a")]
    [InlineData("somenteminusculas")] // 1 tipo de caractere
    [InlineData("minusculas-e-simbolos")] // 2 tipos
    [InlineData(" Espaco-Inicial#1")]
    [InlineData("Aa1#Aa1#Aa1#Aa1#")] // poucos caracteres distintos
    [InlineData("Admin.Local#2026x")] // contém o nome de usuário
    [InlineData("Maria.Silva#2026x")] // contém a parte local do e-mail
    public void Senha_Fora_Da_Politica_E_Recusada(string senha)
        => PoliticaSenha.Validar(senha, "admin.local", "maria.silva@exemplo.com").Should().NotBeNull();

    [Theory]
    [InlineData("Senha-Local#2026")]
    [InlineData("correto cavalo bateria grampo 42")]
    public void Senha_Na_Politica_E_Aceita(string senha)
        => PoliticaSenha.Validar(senha, "admin.local", "maria.silva@exemplo.com").Should().BeNull();

    [Fact]
    public void Motivo_Nunca_Ecoa_A_Senha()
        => PoliticaSenha.Validar("Admin.Local#2026x", "admin.local").Should().NotContain("2026x");
}
