using FluentAssertions;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Web.Ui.Dados;
using PortalBase.Web.Ui.Navegacao;
using PermissoesApp = PortalBase.Application.Autorizacao.Permissoes;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Administração/Operações REAIS: as rotas de administração e operações são telas reais sobre read-models
/// autorizados (nada "em preparação"). Também valida o
/// mapeamento de apresentação (AdminUi): exceção→"sem permissão" e estado/status→badge (cor + texto).
/// </summary>
public sealed class AdministracaoOperacoesReaisUiTests
{
    // ---- Promoção das rotas reais (§5/§48) — o menu não mostra tela que o servidor negaria ----

    [Theory]
    [InlineData("/admin/tenant", PermissoesApp.AdminTenant)]
    [InlineData("/admin/usuarios", PermissoesApp.AdminUsuarios)]
    [InlineData("/dados/arquivos", PermissoesApp.DadosVisualizar)]
    public void Rotas_Reais_Nao_Estao_Em_Preparacao_E_Tem_A_Permissao_Do_Servidor(string rota, string permissao)
    {
        var item = NavegacaoPortal.EncontrarPorRota(rota);
        item.Should().NotBeNull();
        item!.EmPreparacao.Should().BeFalse();
        item.PermissaoNecessaria.Should().Be(permissao);
    }

    [Fact]
    public void Usuarios_Agora_E_Real_Nao_Esta_Em_Preparacao()
    {
        // Prompt 72: /admin/usuarios ganhou read-model REAL de listagem por tenant (ConsultaUsuariosAutorizada)
        // + ações reais (revogar acesso / atribuir papel). Deixou de ser Placeholder/BLOCKED.
        var item = NavegacaoPortal.EncontrarPorRota("/admin/usuarios");
        item.Should().NotBeNull();
        item!.EmPreparacao.Should().BeFalse();
        item.PermissaoNecessaria.Should().Be(PermissoesApp.AdminUsuarios);
    }

    // ---- AdminUi.ResumoSchema: o cartão mostra a data da migração (o nome completo fica na tabela) ----

    [Theory]
    [InlineData("20260918214026_TenantMarcaVisual", "18/09/2026")]
    [InlineData(null, "—")]
    [InlineData("  ", "—")]
    [InlineData("v1-sem-data", "v1-sem-data")]
    public void ResumoSchema_Mostra_A_Data_Da_Migracao(string? versao, string esperado)
        => AdminUi.ResumoSchema(versao).Should().Be(esperado);

    // ---- AdminUi.EhAutorizacao: exceções de autorização/autenticação → "sem permissão" ----

    [Fact]
    public void EhAutorizacao_Verdadeiro_Para_Excecoes_De_Autorizacao_E_Autenticacao()
    {
        AdminUi.EhAutorizacao(new AutorizacaoNegadaException()).Should().BeTrue();
        AdminUi.EhAutorizacao(new NaoAutenticadoException()).Should().BeTrue();
        AdminUi.EhAutorizacao(new TenantNaoAutorizadoException()).Should().BeTrue();
        AdminUi.EhAutorizacao(new MfaRequeridoException()).Should().BeTrue();
    }

    [Fact]
    public void EhAutorizacao_Falso_Para_Outras_Excecoes()
    {
        AdminUi.EhAutorizacao(new InvalidOperationException()).Should().BeFalse();
        AdminUi.EhAutorizacao(new TenantContextAusenteException()).Should().BeFalse(); // "sem tenant" ≠ "sem permissão"
    }

    // ---- AdminUi badges: cor + TEXTO (nunca só cor — §8/§73) ----

    [Theory]
    [InlineData(EstadoProvisionamento.Ativo, "Ativo", "ok")]
    [InlineData(EstadoProvisionamento.Pronto, "Pronto", "ok")]
    [InlineData(EstadoProvisionamento.Provisionando, "Provisionando", "warn")]
    [InlineData(EstadoProvisionamento.Falhado, "Falhado", "bad")]
    [InlineData(EstadoProvisionamento.Desabilitado, "Desabilitado", "neutral")]
    public void EstadoTenant_Mapeia_Rotulo_E_Badge(EstadoProvisionamento estado, string rotulo, string badge)
    {
        var (r, b) = AdminUi.EstadoTenant(estado);
        r.Should().Be(rotulo);
        b.Should().Be(badge);
    }
}
