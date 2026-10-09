using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PortalBase.Web.Ui.Autorizacao;
using PortalBase.Web.Ui.Navegacao;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Menu lateral em dropdown: qual item fica destacado (o de rota mais longa que casa com o endereço — um só) e as
/// chaves estáveis dos grupos (usadas no HTML e na preferência do navegador). O comportamento no navegador está em
/// MenuLateralE2ETests.
/// </summary>
public sealed class MenuLateralDropdownTests
{
    [Theory]
    [InlineData("", "Dashboard")]
    [InlineData("/", "Dashboard")]
    [InlineData("dados/arquivos", "Arquivos")]
    [InlineData("dados/arquivos?estado=Rejeitado#x", "Arquivos")]
    [InlineData("admin", "Administração")]
    [InlineData("admin/usuarios", "Usuários")] // não destaca também "Administração" (/admin)
    [InlineData("admin/usuarios/0f8fad5b-d9cb-469f-a165-70867728950e", "Usuários")]
    [InlineData("Admin/Usuarios/abc", "Usuários")] // rotas do Blazor não diferenciam maiúsculas
    [InlineData("operacoes/integracoes", "Integrações")]
    public void Destaca_Um_Unico_Item_O_De_Rota_Mais_Longa(string caminho, string rotulo)
    {
        var ativo = NavegacaoPortal.ItemAtivo(NavegacaoPortal.Grupos, caminho);

        ativo.Should().NotBeNull();
        ativo!.Rotulo.Should().Be(rotulo);
        NavegacaoPortal.Grupos.SelectMany(g => g.Itens).Count(i => i == ativo).Should().Be(1);
    }

    [Theory]
    [InlineData("conta/minha-conta")]
    [InlineData("dadosx")] // prefixo sem separador não conta
    [InlineData("dados")]
    public void Endereco_Fora_Do_Menu_Nao_Destaca_Nada(string caminho)
        => NavegacaoPortal.ItemAtivo(NavegacaoPortal.Grupos, caminho).Should().BeNull();

    [Fact]
    public void Item_Ativo_Respeita_O_Menu_Filtrado()
    {
        // Sem "Usuários" no menu (filtrado), o endereço dela cai no item que sobrou com o maior prefixo.
        var filtrado = NavegacaoPortal.Grupos
            .Select(g => g with { Itens = g.Itens.Where(i => i.Rota != "/admin/usuarios").ToList() })
            .ToList();

        NavegacaoPortal.ItemAtivo(filtrado, "admin/usuarios")!.Rotulo.Should().Be("Administração");
    }

    [Fact]
    public void So_Principal_Fica_Sempre_Aberto_E_As_Chaves_Sao_Estaveis()
    {
        NavegacaoPortal.Grupos.Where(g => !g.Expansivel).Select(g => g.Titulo).Should().Equal("Principal");
        NavegacaoPortal.Grupos.Where(g => g.Expansivel).Select(g => g.Chave).Should().Equal(
            "dados", "administracao", "configuracoes", "auditoria", "operacoes");
    }

    [Theory]
    [InlineData("admin/contribuintes", true)]
    [InlineData("/configuracoes/email?origem=menu", true)]
    [InlineData("Admin/Contribuintes/", true)]
    [InlineData("admin/contribuintes/x", false)] // só a própria tela, não sub-rotas
    [InlineData("admin/usuarios", false)]
    [InlineData("configuracoes/marca", false)]
    [InlineData("", false)]
    public void So_Telas_De_Plataforma_Abrem_Sem_Contribuinte(string caminho, bool abre)
        => NavegacaoPortal.AbreSemContribuinte(caminho).Should().Be(abre);

    [Fact]
    public async Task Menu_Sem_Contribuinte_Mostra_So_As_Telas_De_Plataforma_Que_O_Usuario_Pode_Abrir()
    {
        static async Task<AutorizacaoVisual> Com(params string[] permissoes)
        {
            var visual = new AutorizacaoVisual(new ConfigurationBuilder().Build(), new ProvedorPermissoesVisuaisFake(permissoes));
            await visual.HidratarAsync();
            return visual;
        }

        var plataforma = await Com([.. PortalBase.Application.Autorizacao.Permissoes.Plataforma, "dados.visualizar", "admin.usuarios"]);
        NavegacaoPortal.FiltrarSemContribuinte(plataforma).SelectMany(g => g.Itens).Select(i => i.Rota)
            .Should().BeEquivalentTo(["/admin/contribuintes", "/configuracoes/email"], "telas de contribuinte não abrem sem contribuinte");

        var soEmail = await Com(PortalBase.Application.Autorizacao.Permissoes.PlataformaEmailAdministrar);
        NavegacaoPortal.FiltrarSemContribuinte(soEmail).SelectMany(g => g.Itens).Select(i => i.Rota)
            .Should().Equal("/configuracoes/email");

        NavegacaoPortal.FiltrarSemContribuinte(await Com("admin.usuarios", "admin.tenant")).Should().BeEmpty(
            "sem permissão de plataforma, nada abre sem contribuinte (o layout leva à seleção)");
    }

    [Fact]
    public void Filtrar_Preserva_Se_O_Grupo_E_Expansivel()
    {
        var todos = new PortalBase.Web.Ui.Autorizacao.AutorizacaoVisual(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Ui:DadosDemonstracao"] = "true" })
                .Build(),
            ProvedorPermissoesVisuaisFake.Vazio);

        NavegacaoPortal.Filtrar(todos).Select(g => g.Expansivel)
            .Should().Equal(NavegacaoPortal.Grupos.Select(g => g.Expansivel));
    }

    [Fact]
    public void Todo_Grupo_E_Item_Do_Menu_Tem_Um_Icone_De_Linha_Do_Catalogo()
    {
        // Os ícones são SVG desenhados pelo próprio portal (sem emoji): o menu recolhido mostra só eles.
        foreach (var grupo in NavegacaoPortal.Grupos)
        {
            IconesLinha.Existe(grupo.Icone).Should().BeTrue($"o grupo {grupo.Titulo} usa o ícone '{grupo.Icone}'");
            grupo.Icone.Should().NotBe(IconesLinha.Neutro, $"o grupo {grupo.Titulo} tem ícone próprio");
            foreach (var item in grupo.Itens)
            {
                IconesLinha.Existe(item.Icone).Should().BeTrue($"o item {item.Rotulo} usa o ícone '{item.Icone}'");
            }
        }
    }

    [Fact]
    public void Icones_Do_Catalogo_Sao_So_Tracos_SVG_E_Nome_Desconhecido_Cai_No_Neutro()
    {
        foreach (var nome in IconesLinha.Nomes)
        {
            IconesLinha.Caminho(nome).Should().MatchRegex("^[MmLlHhVvCcSsQqAaZz0-9 .,-]+$", $"o ícone '{nome}' é só um traço SVG");
        }

        IconesLinha.Caminho("nao-existe").Should().Be(IconesLinha.Caminho(IconesLinha.Neutro));
        IconesLinha.Caminho(null).Should().Be(IconesLinha.Caminho(IconesLinha.Neutro));
    }
}
