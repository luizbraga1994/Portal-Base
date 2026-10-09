using FluentAssertions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Provisionamento;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Provisionamento;

/// <summary>
/// Ciclo de vida operacional do tenant (Prompt 09): ativação, desabilitação e reativação com
/// autorização administrativa server-side, validação de infraestrutura, idempotência,
/// transições inválidas e auditoria. Dobras determinísticas — sem banco/rede.
/// </summary>
public sealed class CicloVidaTenantTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Usuario = Guid.NewGuid();

    private readonly RepositorioTenantFake _repositorio = new();
    private readonly UnitOfWorkFake _unitOfWork = new();
    private readonly ServicoAutorizacaoFake _autorizacao = new() { Permitir = true };
    private readonly ValidadorProvisionamentoFake _validador = new();
    private readonly AuditoriaFake _auditoria = new();
    private readonly ContextoSegurancaFake _seguranca = new() { PrincipalId = Usuario.ToString() };

    private CicloVidaTenant Criar() => new(
        _repositorio, _unitOfWork, _autorizacao, _validador, _auditoria, _seguranca, new RelogioFixo(Momento));

    private static Tenant TenantPronto()
    {
        var tenant = Tenant.Criar(Cnpj.Criar("11444777000161"), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa", "cofre://x", Momento);
        tenant.IniciarProvisionamento(Momento);
        tenant.ConcluirProvisionamento("v1", "v1", Momento);
        return tenant;
    }

    private Tenant Semear(Tenant tenant)
    {
        _repositorio.Adicionados.Add(tenant);
        return tenant;
    }

    private bool TemEvento(string acao) => _auditoria.Eventos.Exists(e => e.Operacao == acao);

    [Fact]
    public async Task Ativar_TenantPronto_TornaAtivo_E_Audita()
    {
        var tenant = Semear(TenantPronto());

        var resultado = await Criar().AtivarAsync(tenant.Id, CancellationToken.None);

        resultado.Sucesso.Should().BeTrue();
        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
        tenant.EstaOperacional.Should().BeTrue();
        _validador.Chamadas.Should().Be(1); // valida a infraestrutura antes de ativar
        TemEvento(AcaoAuditoria.TenantActivated).Should().BeTrue();
    }

    [Fact]
    public async Task Ativar_SemAutorizacao_Lanca_E_NaoAtiva()
    {
        _autorizacao.Permitir = false;
        var tenant = Semear(TenantPronto());

        var acao = async () => await Criar().AtivarAsync(tenant.Id, CancellationToken.None);

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        tenant.Estado.Should().Be(EstadoProvisionamento.Pronto);
    }

    [Fact]
    public async Task Ativar_TenantNaoPronto_Recusa()
    {
        var tenant = Semear(Tenant.Criar(Cnpj.Criar("11444777000161"), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "E", "cofre://x", Momento)); // Pendente

        var resultado = await Criar().AtivarAsync(tenant.Id, CancellationToken.None);

        resultado.Sucesso.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoProvisionamento.Pendente);
    }

    [Fact]
    public async Task Ativar_ComInfraInvalida_Recusa_E_NaoAtiva()
    {
        _validador.Valido = false;
        var tenant = Semear(TenantPronto());

        var resultado = await Criar().AtivarAsync(tenant.Id, CancellationToken.None);

        resultado.Sucesso.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoProvisionamento.Pronto); // não ativa se a infra não valida
    }

    [Fact]
    public async Task Ativar_JaAtivo_EhIdempotente()
    {
        var tenant = TenantPronto();
        tenant.Ativar(Momento);
        Semear(tenant);

        var resultado = await Criar().AtivarAsync(tenant.Id, CancellationToken.None);

        resultado.Sucesso.Should().BeTrue();
        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
    }

    [Fact]
    public async Task Desabilitar_TenantAtivo_Bloqueia_E_Audita()
    {
        var tenant = TenantPronto();
        tenant.Ativar(Momento);
        Semear(tenant);

        var resultado = await Criar().DesabilitarAsync(tenant.Id, CancellationToken.None);

        resultado.Sucesso.Should().BeTrue();
        tenant.Estado.Should().Be(EstadoProvisionamento.Desabilitado);
        tenant.EstaOperacional.Should().BeFalse();
        TemEvento(AcaoAuditoria.TenantDisabled).Should().BeTrue();
    }

    [Fact]
    public async Task Reativar_TenantDesabilitado_TornaAtivo_E_Audita()
    {
        var tenant = TenantPronto();
        tenant.Ativar(Momento);
        tenant.Desabilitar(Momento);
        Semear(tenant);

        var resultado = await Criar().ReativarAsync(tenant.Id, CancellationToken.None);

        resultado.Sucesso.Should().BeTrue();
        tenant.Estado.Should().Be(EstadoProvisionamento.Ativo);
        _validador.Chamadas.Should().Be(1); // revalida a infraestrutura na reativação
        TemEvento(AcaoAuditoria.TenantReactivated).Should().BeTrue();
    }

    [Fact]
    public async Task Reativar_TenantNaoDesabilitado_Recusa()
    {
        var tenant = Semear(TenantPronto()); // Pronto, não Desabilitado

        var resultado = await Criar().ReativarAsync(tenant.Id, CancellationToken.None);

        resultado.Sucesso.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoProvisionamento.Pronto);
    }

    [Fact]
    public async Task Reativar_ComInfraInvalida_Recusa()
    {
        _validador.Valido = false;
        var tenant = TenantPronto();
        tenant.Ativar(Momento);
        tenant.Desabilitar(Momento);
        Semear(tenant);

        var resultado = await Criar().ReativarAsync(tenant.Id, CancellationToken.None);

        resultado.Sucesso.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoProvisionamento.Desabilitado);
    }

    [Fact]
    public async Task Operacao_EmTenantInexistente_Lanca()
    {
        var acao = async () => await Criar().AtivarAsync(Guid.NewGuid(), CancellationToken.None);

        await acao.Should().ThrowAsync<TenantNaoEncontradoException>();
    }
}
