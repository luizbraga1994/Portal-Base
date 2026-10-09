using System.Text.RegularExpressions;
using FluentAssertions;
using PortalBase.Application.Segredos;

namespace PortalBase.Application.Tests.Segredos;

/// <summary>Nomes de segredo determinísticos, server-side, sem dado sensível (seção 15).</summary>
public sealed partial class NomeSegredoTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Nome_Global_E_Deterministico()
    {
        NomeSegredo.AuditoriaHmac("prod").Should().Be("portalbase-prod-audit-hmac");
        NomeSegredo.ProtecaoTotp("prod").Should().Be("portalbase-prod-security-totp-protection");
    }

    [Fact]
    public void Nome_Por_Tenant_Inclui_Identificador_Tecnico()
    {
        var nome = NomeSegredo.SegredoIntegracao("prod", TenantA);
        nome.Should().Contain(TenantA.ToString("N"));
        nome.Should().StartWith("portalbase-prod-tenant-");
    }

    [Fact]
    public void Tenants_Distintos_Geram_Nomes_Distintos()
        => NomeSegredo.SegredoIntegracao("prod", TenantA).Should().NotBe(NomeSegredo.SegredoIntegracao("prod", TenantB));

    [Fact]
    public void Nome_Respeita_Charset_Do_Cofre()
    {
        foreach (var nome in new[]
        {
            NomeSegredo.AuditoriaHmac("Prod-01"),
            NomeSegredo.SegredoIntegracao("test", TenantA),
            NomeSegredo.SegredoIntegracao("dev", TenantB),
        })
        {
            PadraoCofre().IsMatch(nome).Should().BeTrue($"'{nome}' deve conter só [0-9a-zA-Z-]");
            nome.Length.Should().BeLessThanOrEqualTo(127);
        }
    }

    [Fact]
    public void Nome_Por_Tenant_Exige_Tenant()
    {
        var acao = () => NomeSegredo.SegredoIntegracao("prod", Guid.Empty);
        acao.Should().Throw<ArgumentException>();
    }

    [GeneratedRegex("^[0-9a-zA-Z-]+$")]
    private static partial Regex PadraoCofre();
}
