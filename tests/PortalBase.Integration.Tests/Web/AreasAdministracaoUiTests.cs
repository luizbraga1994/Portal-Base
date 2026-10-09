using FluentAssertions;
using PortalBase.Web.Ui.Navegacao;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// P71-R1 (GAP-P71R1-01): o centro de administração (<c>/admin</c>) declara o estado de cada área. Usuários,
/// Tenant e outras áreas eram exibidos como "Lacuna"/"Parcial" depois de ligados ao backend real (P72/P75), e o
/// contador "Áreas com lacuna registrada" mostrava 3 — estado falso. Estes testes amarram cada área a uma rota
/// REAL do menu com a MESMA permissão (menu = página = servidor) e fixam o estado das áreas do escopo do P71.
/// </summary>
public sealed class AreasAdministracaoUiTests
{
    [Fact]
    public void Cada_Area_Aponta_Para_Rota_Do_Menu_Com_A_Mesma_Permissao()
    {
        AreasAdministracao.Todas.Should().NotBeEmpty();
        foreach (var area in AreasAdministracao.Todas)
        {
            var item = NavegacaoPortal.EncontrarPorRota(area.Rota);
            item.Should().NotBeNull($"a área '{area.Titulo}' deve abrir uma rota real do menu");
            item!.EmPreparacao.Should().BeFalse();
            item.PermissaoNecessaria.Should().Be(area.Permissao,
                $"a área '{area.Titulo}' não pode exigir permissão diferente da do menu para '{area.Rota}'");
        }
    }

    [Theory]
    [InlineData("/admin/usuarios")]
    [InlineData("/admin/tenant")]
    public void Areas_Do_Escopo_P71_Estao_Ligadas_E_Nao_Declaram_Lacuna_De_Backend(string rota)
    {
        var area = AreasAdministracao.Todas.Single(a => a.Rota == rota);

        area.EstadoRotulo.Should().Be("Ligada");
        area.EstadoBadge.Should().Be("ok");
        area.Observacao.Should().NotContainEquivalentOf("falta", "o backend desta área existe (P72/P75)");
    }

    [Fact]
    public void Contadores_Do_Painel_Refletem_As_Areas()
    {
        (AreasAdministracao.Ligadas + AreasAdministracao.ComLacuna).Should().Be(AreasAdministracao.Todas.Count);
        AreasAdministracao.ComLacuna.Should().Be(AreasAdministracao.Todas.Count(a => a.EstadoBadge != "ok"));
    }

    [Fact]
    public void Observacoes_Descrevem_O_Que_As_Telas_Fazem_Hoje()
    {
        // As decisões de produto foram tomadas: o contribuinte tem ciclo de vida pela tela
        // e os usuários têm cadastro completo. A observação diz isso — nada de "decisão pendente" desatualizada.
        AreasAdministracao.Todas.Single(a => a.Rota == "/admin/tenant").Observacao.Should().Contain("ciclo de vida");
        AreasAdministracao.Todas.Single(a => a.Rota == "/admin/usuarios").Observacao.Should().Contain("Cadastro");
        AreasAdministracao.Todas.Should().NotContain(a => a.Observacao.Contains("decisão de produto", StringComparison.OrdinalIgnoreCase));

        var contribuintes = AreasAdministracao.Todas.Single(a => a.Rota == "/admin/contribuintes");
        contribuintes.Permissao.Should().Be(PortalBase.Application.Autorizacao.Permissoes.PlataformaTenantProvisionar, "só o Administrador da plataforma");
    }
}
