using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Tests.Dobras;

namespace PortalBase.Application.Tests.Multitenancy;

public sealed class ResolvedorTenantTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string Principal = Guid.NewGuid().ToString();

    private static TenantMetadados Metadados() => new(TenantId, "tenant_abc", "cofre://x", Operacional: true);

    private static ResolvedorTenant Criar(TenantMetadados? metadados, bool autorizado, ContextoTenant contexto, out AuditoriaFake auditoria)
    {
        auditoria = new AuditoriaFake();
        return new ResolvedorTenant(
            new TenantStoreFake(metadados), new AutorizacaoTenantFake(autorizado), contexto, auditoria, new RelogioFixo(Momento));
    }

    [Fact]
    public async Task SemPrincipal_Lanca_E_ContextoFicaInvalido()
    {
        var contexto = new ContextoTenant();
        var resolvedor = Criar(Metadados(), autorizado: true, contexto, out _);

        var acao = async () => await resolvedor.ResolverAsync("  ", TenantId);

        await acao.Should().ThrowAsync<NaoAutenticadoException>();
        contexto.EstaAutorizado.Should().BeFalse();
    }

    [Fact]
    public async Task TenantInexistente_Lanca_NaoEncontrado()
    {
        var contexto = new ContextoTenant();
        var resolvedor = Criar(metadados: null, autorizado: true, contexto, out _);

        var acao = async () => await resolvedor.ResolverAsync(Principal, TenantId);

        await acao.Should().ThrowAsync<TenantNaoEncontradoException>();
        contexto.Estado.Should().Be(EstadoTenant.Invalido);
    }

    [Fact]
    public async Task NaoAutorizado_NaoExpoeBase_E_AuditaNegado()
    {
        var contexto = new ContextoTenant();
        var resolvedor = Criar(Metadados(), autorizado: false, contexto, out var auditoria);

        var acao = async () => await resolvedor.ResolverAsync(Principal, TenantId);

        await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
        contexto.Estado.Should().Be(EstadoTenant.Invalido);
        contexto.EstaAutorizado.Should().BeFalse();
        contexto.NomeBaseFisica.Should().BeNull();
        auditoria.Eventos.Should().ContainSingle().Which.Resultado.Should().Be(ResultadoAuditoria.Negado);
    }

    [Fact]
    public async Task Autorizado_ContextoAutorizado_ExpoeBase_E_AuditaPermitido()
    {
        var contexto = new ContextoTenant();
        var resolvedor = Criar(Metadados(), autorizado: true, contexto, out var auditoria);

        var retornado = await resolvedor.ResolverAsync(Principal, TenantId);

        retornado.EstaAutorizado.Should().BeTrue();
        contexto.EstaAutorizado.Should().BeTrue();
        contexto.TenantId.Should().Be(TenantId);
        contexto.NomeBaseFisica.Should().Be("tenant_abc");
        auditoria.Eventos.Should().ContainSingle().Which.Resultado.Should().Be(ResultadoAuditoria.Permitido);
    }

    [Fact]
    public async Task Troca_DeAutorizadoParaNegado_InvalidaContextoAnterior()
    {
        var contexto = new ContextoTenant();

        var autorizador = new ResolvedorTenant(
            new TenantStoreFake(Metadados()), new AutorizacaoTenantFake(true), contexto, new AuditoriaFake(), new RelogioFixo(Momento));
        await autorizador.ResolverAsync(Principal, TenantId);
        contexto.EstaAutorizado.Should().BeTrue();

        var negador = new ResolvedorTenant(
            new TenantStoreFake(Metadados()), new AutorizacaoTenantFake(false), contexto, new AuditoriaFake(), new RelogioFixo(Momento));
        var acao = async () => await negador.ResolverAsync(Principal, Guid.Parse("22222222-2222-2222-2222-222222222222"));

        await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
        contexto.EstaAutorizado.Should().BeFalse();
        contexto.NomeBaseFisica.Should().BeNull();
    }
}
