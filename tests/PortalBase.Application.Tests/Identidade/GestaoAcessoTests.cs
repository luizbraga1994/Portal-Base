using FluentAssertions;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Identidade;

/// <summary>Administração de acesso: autorização, bloqueio de auto-concessão e revogação (seções 21, 28).</summary>
public sealed class GestaoAcessoTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Ator = Guid.NewGuid();
    private static readonly Guid Alvo = Guid.NewGuid();

    private readonly ContextoSeguranca _seguranca = new();
    private readonly AutorizacaoTenantFakeIdentidade _tenant = new();
    private readonly RepositorioUsuarioFake _usuarios = new();
    private readonly RepositorioAcessoFake _acessos = new();
    private readonly GestaoAcesso _gestao;

    public GestaoAcessoTests()
    {
        var autorizacao = new ServicoAutorizacao(_seguranca, _tenant, _usuarios, new AuditoriaFake(), new RelogioFixo(Momento));
        _gestao = new GestaoAcesso(autorizacao, _seguranca, _acessos, _usuarios, new UnitOfWorkFake(), new AuditoriaFake(), new RelogioFixo(Momento));
    }

    private void ComAtorAdministrador()
    {
        _seguranca.Definir(Ator.ToString(), mfaSatisfeito: false);
        _tenant.Permitidos.Add((Ator.ToString(), Tenant));
        _usuarios.DefinirPermissoes(Ator, Tenant, Permissoes.AdminUsuarios);
    }

    [Fact] // §33.6 — admin concede acesso a outro usuário
    public async Task Admin_Concede_Acesso_A_Outro()
    {
        ComAtorAdministrador();

        await _gestao.ConcederAcessoAsync(Tenant, Alvo, PapelConhecido.Analista);

        _acessos.Vinculos.Should().ContainSingle(v => v.UsuarioId == Alvo && v.TenantId == Tenant);
        _acessos.Papeis.Should().ContainSingle();
    }

    [Fact] // §33.11/12/13 — auto-concessão de acesso é bloqueada
    public async Task Nao_Pode_Conceder_Acesso_A_Si_Mesmo()
    {
        ComAtorAdministrador();

        var acao = async () => await _gestao.ConcederAcessoAsync(Tenant, Ator, PapelConhecido.Administrador);
        await acao.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
    }

    [Fact] // §33.11 — auto-atribuição de papel é bloqueada (ex.: virar Administrador)
    public async Task Nao_Pode_Atribuir_Papel_A_Si_Mesmo()
    {
        ComAtorAdministrador();

        var acao = async () => await _gestao.AtribuirPapelAsync(Tenant, Ator, PapelConhecido.Analista);
        await acao.Should().ThrowAsync<AutoConcessaoNaoPermitidaException>();
    }

    [Fact] // sem permissão de admin → negado (server-side)
    public async Task Ator_Sem_Permissao_De_Admin_E_Negado()
    {
        _seguranca.Definir(Ator.ToString(), mfaSatisfeito: false);
        _tenant.Permitidos.Add((Ator.ToString(), Tenant));
        _usuarios.DefinirPermissoes(Ator, Tenant, Permissoes.DadosVisualizar); // não é admin

        var acao = async () => await _gestao.ConcederAcessoAsync(Tenant, Alvo, PapelConhecido.Analista);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [Fact] // §33.9 / §28 — revogar acesso revoga vínculo e invalida sessões (rotaciona SecurityStamp)
    public async Task Revogar_Acesso_Revoga_Vinculo_E_Rotaciona_Stamp()
    {
        ComAtorAdministrador();
        var alvoUsuario = Usuario.Criar("alvo", "Alvo", "alvo@exemplo.com", Momento);
        _usuarios.Usuarios.Add(alvoUsuario);
        var stampAntes = alvoUsuario.SecurityStamp;
        var vinculo = UsuarioTenant.Conceder(alvoUsuario.Id, Tenant, Momento);
        _acessos.Vinculos.Add(vinculo);

        await _gestao.RevogarAcessoAsync(Tenant, alvoUsuario.Id);

        vinculo.EstaAtivo.Should().BeFalse();
        alvoUsuario.SecurityStamp.Should().NotBe(stampAntes);
    }
}
