using FluentAssertions;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Domain.Tests.Catalogo;

/// <summary>
/// O prefixo técnico do portal (<c>portalbase</c>, trocado pelo <c>-n</c> do <c>dotnet new</c>) entra em identificadores
/// do MySQL que têm limite de tamanho: nome de base (64) e de conta (32). Um portal criado com nome longo demais
/// aceitaria contribuintes cuja base NÃO pode ser criada (o provisionamento falharia só na hora). Estes testes falham
/// no portal gerado antes disso: use no <c>-n</c> até 16 caracteres (ver docs/COMO-USAR-O-TEMPLATE.md).
/// </summary>
public sealed class LimitesNomePortalTests
{
    private const int LimiteNomeBaseMySql = 64;
    private const int LimiteNomeContaMySql = 32;
    private const string Orientacao = "o nome do portal (-n do dotnet new) ficou longo demais: use até 16 caracteres";

    private static string PrefixoPortal => NomeTenant.PrefixoBaseFisica[..^"_tenant_".Length];

    [Fact]
    public void Base_Do_Contribuinte_Com_O_Maior_Nome_Aceito_Cabe_No_Limite_Do_MySql()
    {
        var maior = NomeTenant.Criar("T" + new string('X', NomeTenant.TamanhoMaximo - 1));

        maior.Valor.Should().HaveLength(NomeTenant.TamanhoMaximo);
        maior.NomeBaseFisica.Length.Should().BeLessThanOrEqualTo(LimiteNomeBaseMySql, Orientacao);
    }

    [Theory]
    [InlineData("_app")]
    [InlineData("_admin")]
    [InlineData("_migrador")]
    public void Contas_Do_MySql_Do_Portal_Cabem_No_Limite(string sufixo)
    {
        // As mesmas contas que scripts/configurar-ambiente-local.ps1 e scripts/configurar-iis.ps1 criam por padrão.
        (PrefixoPortal + sufixo).Length.Should().BeLessThanOrEqualTo(LimiteNomeContaMySql, Orientacao);
    }

    [Fact]
    public void Prefixo_Do_Portal_So_Tem_Letras_Minusculas_E_Digitos()
    {
        // Vira nome de base, conta, cookie e prefixo de segredo: nada de espaço, hífen ou acento.
        PrefixoPortal.Should().MatchRegex("^[a-z][a-z0-9]*$");
    }
}
