using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Catalogo;
using PortalBase.Web.Ui.Autorizacao;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Prompt 71 (F70-CRIT-01): a autorização VISUAL, FORA do modo demonstração, é uma PROJEÇÃO REAL das
/// permissões efetivas do usuário autenticado, resolvida no servidor e hidratada no início do circuito.
/// Estes testes exercem o <see cref="AutorizacaoVisual"/> hidratado por um provedor, e o provedor REAL
/// (<see cref="ProvedorPermissoesVisuais"/>) contra fakes das fontes do servidor — cobrindo com/sem
/// permissão, múltiplos papéis, sem papel, não autenticado, tenant ausente/errado, ausência de fonte
/// (fail-closed), plataforma, mudança de permissões e a INDEPENDÊNCIA do backend (a UI nunca é autoridade).
/// Determinístico, sem MySQL.
/// </summary>
public sealed class AutorizacaoVisualRealTests
{
    private static IConfiguration ConfigProducao() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ui:DadosDemonstracao"] = "false",
        }).Build();

    // ---------- AutorizacaoVisual hidratado por provedor ----------

    [Fact]
    public async Task A_Usuario_Com_Permissao_Ve_Apos_Hidratar()
    {
        var visual = new AutorizacaoVisual(ConfigProducao(), new ProvedorPermissoesVisuaisFake(Permissoes.DadosVisualizar, Permissoes.AdminUsuarios));

        visual.Pode(Permissoes.DadosVisualizar).Should().BeFalse("antes de hidratar é fail-closed");
        await visual.HidratarAsync();

        visual.Pode(Permissoes.DadosVisualizar).Should().BeTrue();
        visual.Pode(Permissoes.AdminUsuarios).Should().BeTrue();
        visual.Demonstracao.Should().BeFalse();
    }

    [Fact]
    public async Task B_Usuario_Sem_A_Permissao_Nao_Ve()
    {
        var visual = new AutorizacaoVisual(ConfigProducao(), new ProvedorPermissoesVisuaisFake(Permissoes.DadosVisualizar));
        await visual.HidratarAsync();

        visual.Pode(Permissoes.AdminTenant).Should().BeFalse();
        visual.Pode(Permissoes.DadosExcluir).Should().BeFalse();
    }

    [Fact]
    public async Task C_D_Multiplos_Papeis_Sao_A_Uniao_Deny_By_Default()
    {
        // Conjunto efetivo = união dos papéis; o que nenhum papel concede permanece negado.
        var visual = new AutorizacaoVisual(ConfigProducao(),
            new ProvedorPermissoesVisuaisFake(Permissoes.DadosVisualizar, Permissoes.DadosCadastrar, Permissoes.DadosExcluir));
        await visual.HidratarAsync();

        visual.PossuiTodas(Permissoes.DadosVisualizar, Permissoes.DadosExcluir).Should().BeTrue();
        visual.PossuiAlguma(Permissoes.AdminTenant, Permissoes.DadosVisualizar).Should().BeTrue();
        visual.Pode(Permissoes.AdminUsuarios).Should().BeFalse(); // nenhum papel concedeu → negado
    }

    [Fact]
    public async Task E_Usuario_Sem_Papel_Fica_Vazio()
    {
        var visual = new AutorizacaoVisual(ConfigProducao(), ProvedorPermissoesVisuaisFake.Vazio);
        await visual.HidratarAsync();

        visual.Permissoes.Should().BeEmpty();
        visual.Pode(Permissoes.DadosVisualizar).Should().BeFalse();
        visual.Pode(null).Should().BeTrue(); // item sem permissão exigida sempre visível
    }

    [Fact]
    public async Task I_Mudanca_De_Permissoes_Reflete_Na_Rehidratacao()
    {
        var config = ConfigProducao();
        var visual = new AutorizacaoVisual(config, new ProvedorPermissoesVisuaisFake(Permissoes.DadosVisualizar));
        await visual.HidratarAsync();
        visual.Pode(Permissoes.AdminTenant).Should().BeFalse();

        // Nova resolução (ex.: papel concedido) — outra hidratação reflete o novo conjunto.
        var visual2 = new AutorizacaoVisual(config, new ProvedorPermissoesVisuaisFake(Permissoes.DadosVisualizar, Permissoes.AdminTenant));
        await visual2.HidratarAsync();
        visual2.Pode(Permissoes.AdminTenant).Should().BeTrue();
    }

    // ---------- Provedor REAL contra fakes do servidor (fail-closed) ----------

    [Fact]
    public async Task F_Nao_Autenticado_Projeta_Vazio()
    {
        var provedor = new ProvedorPermissoesVisuais(
            new SegurancaFake(autenticado: false, principalId: null),
            new TenantAtualFake(Guid.NewGuid()),
            new RepositorioUsuarioFake(new PermissoesTenant([Permissoes.DadosVisualizar])),
            new PlataformaFake(admin: true));

        (await provedor.ResolverAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task G_Sem_Tenant_Selecionado_Nao_Tem_Permissoes_De_Tenant()
    {
        var provedor = new ProvedorPermissoesVisuais(
            new SegurancaFake(autenticado: true, principalId: Guid.NewGuid().ToString()),
            new TenantAtualFake(tenant: null),
            new RepositorioUsuarioFake(new PermissoesTenant([Permissoes.DadosVisualizar])),
            new PlataformaFake(admin: false));

        (await provedor.ResolverAsync()).Should().BeEmpty(); // repo não é consultado sem tenant
    }

    [Fact]
    public async Task Tenant_Selecionado_Projeta_As_Permissoes_Do_Tenant()
    {
        var provedor = new ProvedorPermissoesVisuais(
            new SegurancaFake(autenticado: true, principalId: Guid.NewGuid().ToString()),
            new TenantAtualFake(Guid.NewGuid()),
            new RepositorioUsuarioFake(new PermissoesTenant([Permissoes.DadosVisualizar, Permissoes.DadosImportar])),
            new PlataformaFake(admin: false));

        var conjunto = await provedor.ResolverAsync();

        conjunto.Should().Contain(Permissoes.DadosVisualizar);
        conjunto.Should().Contain(Permissoes.DadosImportar);
        conjunto.Should().NotContain(Permissoes.SegurancaIncidentesGerir); // não é admin de plataforma
    }

    [Fact]
    public async Task Administrador_De_Plataforma_Recebe_As_Permissoes_Globais()
    {
        var provedor = new ProvedorPermissoesVisuais(
            new SegurancaFake(autenticado: true, principalId: Guid.NewGuid().ToString()),
            new TenantAtualFake(Guid.NewGuid()),
            new RepositorioUsuarioFake(new PermissoesTenant([Permissoes.DadosVisualizar])),
            new PlataformaFake(admin: true));

        var conjunto = await provedor.ResolverAsync();

        conjunto.Should().Contain(Permissoes.DadosVisualizar);
        conjunto.Should().Contain(Permissoes.SegurancaIncidentesGerir);
        conjunto.Should().Contain(Permissoes.PlataformaTenantProvisionar);
        conjunto.Should().BeEquivalentTo(new[] { Permissoes.DadosVisualizar }.Concat(Permissoes.Plataforma));
    }

    [Fact]
    public async Task H_Ausencia_Da_Fonte_E_Fail_Closed()
    {
        // O repositório lança (ex.: banco indisponível) — a projeção NÃO concede nada por isso.
        var provedor = new ProvedorPermissoesVisuais(
            new SegurancaFake(autenticado: true, principalId: Guid.NewGuid().ToString()),
            new TenantAtualFake(Guid.NewGuid()),
            new RepositorioUsuarioFake(excecao: new InvalidOperationException("banco indisponível")),
            new PlataformaFake(admin: false));

        (await provedor.ResolverAsync()).Should().BeEmpty();
    }

    // ---------- Backend continua sendo a autoridade (a UI nunca decide) ----------

    [Fact]
    public async Task L_Visual_Pode_Nao_Substitui_A_Autorizacao_Do_Servidor()
    {
        // A UI mostra a ação (tem a permissão no conjunto), mas o servidor NEGA por falta de MFA (step-up).
        var visual = new AutorizacaoVisual(ConfigProducao(), new ProvedorPermissoesVisuaisFake(Permissoes.DadosExcluir));
        await visual.HidratarAsync();
        visual.Pode(Permissoes.DadosExcluir).Should().BeTrue();
        visual.ExigeMfa(Permissoes.DadosExcluir).Should().BeTrue();

        var tenantId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();
        var servico = new ServicoAutorizacao(
            new SegurancaFake(autenticado: true, principalId: usuarioId.ToString(), mfaSatisfeito: false),
            new AutorizacaoTenantFake(pode: true),
            new RepositorioUsuarioFake(new PermissoesTenant([Permissoes.DadosExcluir]), usuarioId),
            new AuditoriaFake(),
            new RelogioFake());

        var decisao = await servico.AvaliarAsync(tenantId, Permissoes.DadosExcluir);

        decisao.Permitido.Should().BeFalse();
        decisao.Motivo.Should().Be(MotivoNegacao.MfaRequerido); // servidor é a autoridade final
    }

    // ================= Fakes =================

    private sealed class SegurancaFake(bool autenticado, string? principalId, bool mfaSatisfeito = false) : IContextoSeguranca
    {
        public bool EstaAutenticado => autenticado;
        public string? PrincipalId => principalId;
        public bool MfaSatisfeito => mfaSatisfeito;
    }

    private sealed class TenantAtualFake(Guid? tenant) : PortalBase.Web.Seguranca.IContextoTenantAtual
    {
        public Guid? TenantAtual => tenant;
    }

    private sealed class AutorizacaoTenantFake(bool pode) : IAutorizacaoTenant
    {
        public ValueTask<bool> PodeAcessarAsync(string? principalId, Guid tenantId, CancellationToken cancellationToken = default) => ValueTask.FromResult(pode);
        public ValueTask<bool> PodeAdministrarAsync(string? principalId, Guid tenantId, CancellationToken cancellationToken = default) => ValueTask.FromResult(pode);
    }

    private sealed class PlataformaFake(bool admin) : IAutorizacaoPlataforma
    {
        public ValueTask<ResultadoAutorizacao> AvaliarAsync(string permissaoGlobal, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(admin ? ResultadoAutorizacao.Ok : ResultadoAutorizacao.Negar(MotivoNegacao.SemPermissao));

        public ValueTask ExigirAsync(string permissaoGlobal, CancellationToken cancellationToken = default) =>
            admin ? ValueTask.CompletedTask : throw new PortalBase.Application.Excecoes.AutorizacaoNegadaException();
    }

    private sealed class RepositorioUsuarioFake : IRepositorioUsuario
    {
        private readonly PermissoesTenant? _permissoes;
        private readonly Exception? _excecao;
        private readonly Guid? _usuarioEsperado;

        public RepositorioUsuarioFake(PermissoesTenant permissoes, Guid? usuarioEsperado = null)
        {
            _permissoes = permissoes;
            _usuarioEsperado = usuarioEsperado;
        }

        public RepositorioUsuarioFake(Exception excecao) => _excecao = excecao;

        public Task<PermissoesTenant> ObterPermissoesAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default)
        {
            if (_excecao is not null)
            {
                throw _excecao;
            }

            if (_usuarioEsperado is Guid esperado)
            {
                usuarioId.Should().Be(esperado);
            }

            return Task.FromResult(_permissoes ?? PermissoesTenant.Nenhuma);
        }

        public Task<bool> EhAdministradorPlataformaAsync(Guid usuarioId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> ExisteAdministradorPlataformaAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<Usuario?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Usuario?> ObterPorLoginAsync(string login, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Usuario?> ObterPorEmailAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> ContarTokensRecuperacaoDesdeAsync(Guid usuarioId, DateTimeOffset desde, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ConsumirTokenRecuperacaoAsync(Guid tokenId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task InvalidarTokensRecuperacaoAsync(Guid usuarioId, DateTimeOffset agoraUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AdicionarAsync(Usuario usuario, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AdicionarCodigosRecuperacaoMfaAsync(IEnumerable<CodigoRecuperacaoMfa> codigos, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RemoverCodigosRecuperacaoMfaAsync(Guid usuarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodigoRecuperacaoMfa?> ObterCodigoRecuperacaoMfaAsync(Guid usuarioId, string hashCodigo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AdicionarTokenRecuperacaoAsync(TokenRecuperacaoConta token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TokenRecuperacaoConta?> ObterTokenRecuperacaoAsync(string hashToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AuditoriaFake : PortalBase.Application.Auditoria.IAuditoria
    {
        public ValueTask RegistrarAsync(PortalBase.Application.Auditoria.EventoAuditoria evento, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RelogioFake : PortalBase.Application.Abstracoes.IRelogio
    {
        public DateTimeOffset AgoraUtc => DateTimeOffset.UnixEpoch;
    }
}
