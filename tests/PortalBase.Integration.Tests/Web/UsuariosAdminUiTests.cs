using FluentAssertions;
using PortalBase.Application.Administracao;
using PortalBase.Application.Excecoes;
using PortalBase.Domain.Catalogo;
using PortalBase.Web.Ui.Dados;
using PortalBase.Web.Ui.Navegacao;
using PortalBase.Web.Ui.Notificacoes;
using PermissoesApp = PortalBase.Application.Autorizacao.Permissoes;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Administração de USUÁRIOS por tenant na UI (Prompt 72): a rota deixa de estar "em preparação" (tela
/// real), o mapeamento de apresentação (status/acesso → badge com cor + TEXTO), a projeção de VM sem
/// segredos, e o mapeador de resultado de AÇÃO (exceção → estado; roteamento para o toast correto).
/// </summary>
public sealed class UsuariosAdminUiTests
{
    // ---- Promoção da rota real (§9) ----

    [Fact]
    public void Rota_Usuarios_Nao_Esta_Em_Preparacao_E_Tem_A_Permissao_Do_Servidor()
    {
        var item = NavegacaoPortal.EncontrarPorRota("/admin/usuarios");
        item.Should().NotBeNull();
        item!.EmPreparacao.Should().BeFalse();
        item.PermissaoNecessaria.Should().Be(PermissoesApp.AdminUsuarios);
    }

    // ---- Badges: cor + TEXTO (nunca só cor) ----

    [Theory]
    [InlineData(StatusUsuario.Ativo, "Ativo", "ok")]
    [InlineData(StatusUsuario.Inativo, "Inativo", "neutral")]
    [InlineData(StatusUsuario.Bloqueado, "Bloqueado", "bad")]
    public void Status_Mapeia_Rotulo_E_Badge(StatusUsuario status, string rotulo, string badge)
    {
        var (r, b) = UsuariosAdminUi.Status(status);
        r.Should().Be(rotulo);
        b.Should().Be(badge);
    }

    [Theory]
    [InlineData(StatusAcesso.Ativo, "Ativo", "ok")]
    [InlineData(StatusAcesso.Revogado, "Revogado", "bad")]
    public void Acesso_Mapeia_Rotulo_E_Badge(StatusAcesso status, string rotulo, string badge)
    {
        var (r, b) = UsuariosAdminUi.Acesso(status);
        r.Should().Be(rotulo);
        b.Should().Be(badge);
    }

    [Fact]
    public void Mapear_Projeta_Campos_Seguros_E_Junta_Papeis()
    {
        var dto = new UsuarioAdministracaoDto(
            Guid.NewGuid(), "joao", "João", "joao@ex.com", StatusUsuario.Ativo, StatusAcesso.Ativo,
            MfaHabilitado: true, BloqueioTemporarioAteUtc: null, Papeis: ["Analista", "Leitor"],
            CriadoEmUtc: DateTimeOffset.UtcNow, UltimoAcessoUtc: null, VinculoConcedidoEmUtc: DateTimeOffset.UtcNow);

        var vm = UsuariosAdminUi.Mapear(dto);

        vm.Login.Should().Be("joao");
        vm.StatusRotulo.Should().Be("Ativo");
        vm.AcessoBadge.Should().Be("ok");
        vm.MfaHabilitado.Should().BeTrue();
        vm.Papeis.Should().Be("Analista, Leitor");
    }

    [Fact]
    public void UsuarioAdminItemVm_NaoExpoe_Segredo()
    {
        foreach (var nome in new[] { "HashSenha", "SegredoMfaProtegido", "SecurityStamp", "ConcurrencyStamp", "Senha" })
        {
            typeof(UsuarioAdminItemVm).GetProperty(nome).Should().BeNull();
        }
    }

    // ---- Mapeador de AÇÃO: exceção → estado seguro ----

    [Fact]
    public void MapearExcecao_Roteia_Cada_Excecao_Para_O_Estado_Correto()
    {
        MapeadorAcaoAdmin.MapearExcecao(new TenantContextAusenteException()).Estado.Should().Be(EstadoAcaoUi.SemTenant);
        MapeadorAcaoAdmin.MapearExcecao(new MfaRequeridoException()).Estado.Should().Be(EstadoAcaoUi.MfaRequerida);
        MapeadorAcaoAdmin.MapearExcecao(new AutorizacaoNegadaException()).Estado.Should().Be(EstadoAcaoUi.SemPermissao);
        MapeadorAcaoAdmin.MapearExcecao(new NaoAutenticadoException()).Estado.Should().Be(EstadoAcaoUi.SemPermissao);
        MapeadorAcaoAdmin.MapearExcecao(new AutoConcessaoNaoPermitidaException()).Estado.Should().Be(EstadoAcaoUi.Invalido);
        MapeadorAcaoAdmin.MapearExcecao(new PortalBase.Domain.Excecoes.EstadoInvalidoException("x")).Estado.Should().Be(EstadoAcaoUi.Conflito);
        MapeadorAcaoAdmin.MapearExcecao(new PortalBase.Domain.Excecoes.EntradaInvalidaException("x")).Estado.Should().Be(EstadoAcaoUi.Invalido);
        MapeadorAcaoAdmin.MapearExcecao(new InvalidOperationException()).Estado.Should().Be(EstadoAcaoUi.Erro);
    }

    // ---- Roteamento para o toast (sucesso/aviso/erro) ----

    [Fact]
    public void Notificar_Roteia_Para_O_Toast_Correto()
    {
        var n = new ServicoNotificacoes();

        MapeadorAcaoAdmin.Notificar(n, new ResultadoAcaoVm(true, EstadoAcaoUi.Ok, "ok", ""));
        n.Ativas[^1].Tipo.Should().Be("ok");

        MapeadorAcaoAdmin.Notificar(n, new ResultadoAcaoVm(false, EstadoAcaoUi.MfaRequerida, "mfa", ""));
        n.Ativas[^1].Tipo.Should().Be("warn");

        MapeadorAcaoAdmin.Notificar(n, new ResultadoAcaoVm(false, EstadoAcaoUi.Conflito, "conflito", ""));
        n.Ativas[^1].Tipo.Should().Be("warn");

        MapeadorAcaoAdmin.Notificar(n, new ResultadoAcaoVm(false, EstadoAcaoUi.SemPermissao, "negado", ""));
        n.Ativas[^1].Tipo.Should().Be("bad");

        MapeadorAcaoAdmin.Notificar(n, new ResultadoAcaoVm(false, EstadoAcaoUi.Erro, "erro", ""));
        n.Ativas[^1].Tipo.Should().Be("bad");
    }
}
