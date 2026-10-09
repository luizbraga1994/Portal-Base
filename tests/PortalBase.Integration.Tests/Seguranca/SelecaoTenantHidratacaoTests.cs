using FluentAssertions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Web.Seguranca;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Hidratação do contribuinte selecionado a partir da escolha persistida (claim), no início do render.
/// É FAIL-CLOSED: se a re-resolução no servidor falhar (vínculo revogado, tenant não operacional),
/// NENHUM contribuinte fica selecionado e a exceção NÃO propaga (para não quebrar o render). O
/// identificador persistido nunca autoriza sozinho — passa sempre pelo <see cref="IResolvedorTenant"/>.
/// </summary>
public sealed class SelecaoTenantHidratacaoTests
{
    private static readonly Guid TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Hidratar_Quando_Resolucao_Falha_Nao_Seleciona_E_Nao_Propaga()
    {
        var selecao = new SelecaoTenant(new ResolvedorFalha(), new SegurancaFake("user-1"), new AuditoriaNula(), new RelogioFake());

        var acao = async () => await selecao.HidratarAsync(TenantId);

        await acao.Should().NotThrowAsync();
        selecao.TenantAtual.Should().BeNull();
    }

    [Fact]
    public async Task Hidratar_Quando_Resolucao_Autoriza_Seleciona_O_Tenant()
    {
        var selecao = new SelecaoTenant(new ResolvedorOk(), new SegurancaFake("user-1"), new AuditoriaNula(), new RelogioFake());

        await selecao.HidratarAsync(TenantId);

        selecao.TenantAtual.Should().Be(TenantId);
    }

    [Fact]
    public async Task Hidratar_Com_Tenant_Vazio_Nao_Seleciona()
    {
        var selecao = new SelecaoTenant(new ResolvedorOk(), new SegurancaFake("user-1"), new AuditoriaNula(), new RelogioFake());

        await selecao.HidratarAsync(Guid.Empty);

        selecao.TenantAtual.Should().BeNull();
    }

    private sealed class ResolvedorFalha : IResolvedorTenant
    {
        public ValueTask<ITenantContext> ResolverAsync(string? principalId, Guid tenantId, CancellationToken cancellationToken = default)
            => throw new TenantNaoAutorizadoException();
    }

    private sealed class ResolvedorOk : IResolvedorTenant
    {
        public ValueTask<ITenantContext> ResolverAsync(string? principalId, Guid tenantId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITenantContext>(new TenantContextFake(tenantId));
    }

    private sealed class TenantContextFake(Guid tenantId) : ITenantContext
    {
        public EstadoTenant Estado => EstadoTenant.Autorizado;
        public string? PrincipalId => "user-1";
        public Guid? TenantId => tenantId;
        public string? NomeBaseFisica => "tenant_x";
        public string? ReferenciaSegredoConexao => "cofre://x";
        public bool EstaAutorizado => true;
    }

    private sealed class SegurancaFake(string? id) : IContextoSeguranca
    {
        public bool EstaAutenticado => id is not null;
        public string? PrincipalId => id;
        public bool MfaSatisfeito => true;
    }

    private sealed class AuditoriaNula : IAuditoria
    {
        public ValueTask RegistrarAsync(EventoAuditoria evento, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RelogioFake : IRelogio
    {
        public DateTimeOffset AgoraUtc => DateTimeOffset.UnixEpoch;
    }
}
