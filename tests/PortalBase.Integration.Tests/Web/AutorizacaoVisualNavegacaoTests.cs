using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PortalBase.Application.Autorizacao;
using PortalBase.Web.Ui.Autorizacao;
using PortalBase.Web.Ui.Navegacao;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Testes da autorização VISUAL e da filtragem de menu por RBAC (Prompt 35, §7/§83). Garante que
/// a UI só oferece itens permitidos e que, sem permissões, é fail-closed (só itens sem permissão).
/// A autorização real permanece no servidor (IServicoAutorizacao) — aqui só a camada de exibição.
/// </summary>
public sealed class AutorizacaoVisualNavegacaoTests
{
    private static IConfiguration Config(bool demonstracao, string? perfil = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ui:DadosDemonstracao"] = demonstracao.ToString(),
            ["Ui:PerfilDemonstracao"] = perfil,
        }).Build();

    private static List<string> RotulosVisiveis(IAutorizacaoVisual visual) =>
        NavegacaoPortal.Filtrar(visual).SelectMany(g => g.Itens).Select(i => i.Rotulo).ToList();

    [Fact]
    public void Demonstracao_Completo_Ve_Todos_Os_Grupos()
    {
        var visual = new AutorizacaoVisual(Config(demonstracao: true, perfil: "Completo"), ProvedorPermissoesVisuaisFake.Vazio);

        NavegacaoPortal.Filtrar(visual).Should().HaveCount(NavegacaoPortal.Grupos.Count);
    }

    [Fact]
    public void Leitor_Ve_Telas_De_Leitura_Mas_Nao_Administracao()
    {
        var visual = new AutorizacaoVisual(Config(demonstracao: true, perfil: "Leitor"), ProvedorPermissoesVisuaisFake.Vazio);

        var rotulos = RotulosVisiveis(visual);

        // Telas de LEITURA exigem dados.visualizar (que o Leitor tem); as de administração, não aparecem.
        rotulos.Should().Contain("Dashboard");
        rotulos.Should().Contain("Arquivos");           // /dados/arquivos (dados.visualizar; enviar exige dados.importar)
        rotulos.Should().NotContain("Usuários");        // exige admin.usuarios
        rotulos.Should().NotContain("Tenant");          // exige admin.tenant
    }

    [Fact]
    public void Sem_Demonstracao_E_Fail_Closed_Apenas_Dashboard()
    {
        // Fora de demonstração, sem tenant/permissões resolvidos, só itens sem permissão exigida aparecem.
        var visual = new AutorizacaoVisual(Config(demonstracao: false), ProvedorPermissoesVisuaisFake.Vazio);

        RotulosVisiveis(visual).Should().ContainSingle().Which.Should().Be("Dashboard");
        visual.Pode(Permissoes.DadosVisualizar).Should().BeFalse();
        visual.Pode(null).Should().BeTrue();
    }

    [Fact]
    public void Perfil_Leitor_Tem_Visualizar_Mas_Nao_Admin()
    {
        var perms = PerfisDemonstracao.Resolver("Leitor");

        perms.Should().Contain(Permissoes.DadosVisualizar);
        perms.Should().NotContain(Permissoes.AdminTenant);
    }

    [Fact]
    public void Excluir_Dados_Exige_Mfa()
    {
        var visual = new AutorizacaoVisual(Config(demonstracao: true), ProvedorPermissoesVisuaisFake.Vazio);

        visual.ExigeMfa(Permissoes.DadosExcluir).Should().BeTrue();
        visual.ExigeMfa(Permissoes.DadosVisualizar).Should().BeFalse();
    }

    [Fact]
    public void DefinirPerfil_Fora_De_Demonstracao_Nao_Concede_Acesso()
    {
        var visual = new AutorizacaoVisual(Config(demonstracao: false), ProvedorPermissoesVisuaisFake.Vazio);

        visual.DefinirPerfil("Completo");

        visual.Permissoes.Should().BeEmpty(); // trocar perfil visual não concede permissão real
    }

    [Fact]
    public void EncontrarPorRota_Resolve_Arquivos()
    {
        NavegacaoPortal.EncontrarPorRota("/dados/arquivos")!.Rotulo.Should().Be("Arquivos");
        NavegacaoPortal.EncontrarPorRota("/rota/inexistente").Should().BeNull();
    }

    [Theory]
    [InlineData("/dados/arquivos")]
    public void Menu_Do_Grupo_Dados_Usa_A_Permissao_Da_Pagina(string rota)
    {
        // Prompt 70: a permissão do item de menu = a EXIGIDA para VER a página (dados.visualizar, o mesmo
        // guard da tela). O menu não pode esconder uma tela que o usuário consegue abrir; as ações de
        // escrita (enviar arquivo) continuam gated à parte no servidor.
        NavegacaoPortal.EncontrarPorRota(rota)!.PermissaoNecessaria.Should().Be(Permissoes.DadosVisualizar);
    }

    private sealed class VisualFixo(params string[] permissoes) : IAutorizacaoVisual
    {
        private readonly HashSet<string> _permissoes = new(permissoes, StringComparer.Ordinal);

        public bool Demonstracao => false;

        public string Perfil => string.Empty;

        public IReadOnlySet<string> Permissoes => _permissoes;

        public bool Pode(string? permissao) => permissao is null || _permissoes.Contains(permissao);

        public bool PossuiAlguma(params string[] permissoes) => permissoes.Any(_permissoes.Contains);

        public bool PossuiTodas(params string[] permissoes) => permissoes.All(_permissoes.Contains);

        public bool ExigeMfa(string permissao) => false;

        public void DefinirPerfil(string perfil) { }

        public Task HidratarAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(PortalBase.Application.Autorizacao.Permissoes.AdminUsuarios)]
    [InlineData(PortalBase.Application.Autorizacao.Permissoes.AdminTenant)]
    public void Centro_De_Administracao_Aparece_Para_Quem_Administra_Qualquer_Area(string permissao)
    {
        var rotas = NavegacaoPortal.Filtrar(new VisualFixo(permissao)).SelectMany(g => g.Itens).Select(i => i.Rota).ToList();

        rotas.Should().Contain("/admin", "a página /admin mostra as áreas que o perfil pode administrar");
        if (permissao != PortalBase.Application.Autorizacao.Permissoes.AdminUsuarios)
        {
            rotas.Should().NotContain("/admin/usuarios");
        }
    }

    [Fact]
    public void Sem_Permissao_De_Administracao_O_Centro_Nao_Aparece()
        => NavegacaoPortal.Filtrar(new VisualFixo(PortalBase.Application.Autorizacao.Permissoes.DadosVisualizar))
            .SelectMany(g => g.Itens).Select(i => i.Rota).Should().NotContain("/admin");
}
