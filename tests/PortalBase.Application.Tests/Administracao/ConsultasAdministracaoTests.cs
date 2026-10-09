using FluentAssertions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Administracao;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Segredos;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Administracao;

/// <summary>
/// Read-models ADMINISTRATIVOS reais (Prompt 71): tenant corrente. Verifica o essencial de segurança (regras 3, 4, 5, 8, 16):
/// autorização SERVER-SIDE fail-closed (lança se não autorizada), escopo do tenant do CONTEXTO (nunca
/// do cliente) e PROJEÇÃO só de campos NÃO secretos — nunca base física, referência de segredo,
/// nem o TEXTO interno de erro.
/// </summary>
public sealed class ConsultasAdministracaoTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ===== ConsultaTenantAutorizada =====

    [Fact]
    public async Task Tenant_SemTenantNoContexto_RetornaNull_SemAutorizarNemConsultar()
    {
        var autz = new AutorizacaoServicoFake();
        var repo = new RepositorioTenantFake(null);
        var consulta = new ConsultaTenantAutorizada(autz, new ContextoTenantFake(null), repo);

        var detalhe = await consulta.ObterAtualAsync();

        detalhe.Should().BeNull();
        autz.Chamadas.Should().Be(0); // sem tenant no contexto, nem chega a autorizar
        repo.ChamadasObterPorId.Should().Be(0);
    }

    [Fact]
    public async Task Tenant_NaoAutorizado_Propaga_FailClosed_SemConsultarRepositorio()
    {
        var autz = new AutorizacaoServicoFake { Negar = true };
        var repo = new RepositorioTenantFake(TenantValido());
        var consulta = new ConsultaTenantAutorizada(autz, new ContextoTenantFake(TenantId), repo);

        var acao = () => consulta.ObterAtualAsync();

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        autz.PermissaoAdministrativa.Should().Be(Permissoes.AdminTenant); // exige admin.tenant (administrativo)
        repo.ChamadasObterPorId.Should().Be(0); // fail-closed: não tocou o dado
    }

    [Fact]
    public async Task Tenant_Autorizado_ProjetaSomenteCamposNaoSecretos()
    {
        var autz = new AutorizacaoServicoFake();
        var repo = new RepositorioTenantFake(TenantValido());
        var consulta = new ConsultaTenantAutorizada(autz, new ContextoTenantFake(TenantId), repo);

        var d = await consulta.ObterAtualAsync();

        d.Should().NotBeNull();
        d!.Id.Should().Be(TenantId);
        d.Nome.Should().Be("STAKE"); // nome escolhido (normalizado), exibido na administração
        d.Cnpj.Should().Be("11444777000161");
        d.RazaoSocial.Should().Be("Empresa Contribuinte");
        d.Estado.Should().Be(EstadoProvisionamento.Pronto);
        d.Operacional.Should().BeFalse(); // Pronto ainda não é operacional (só Ativo)
        d.SchemaVersion.Should().Be("v1");
        d.ComErro.Should().BeFalse();
        autz.PermissaoAdministrativa.Should().Be(Permissoes.AdminTenant);

        // DetalheTenant não tem propriedade de base física / referência de segredo (garantia de compilação):
        typeof(DetalheTenant).GetProperty("NomeBaseFisica").Should().BeNull();
        typeof(DetalheTenant).GetProperty("ReferenciaSegredoConexao").Should().BeNull();
        typeof(DetalheTenant).GetProperty("UltimoErro").Should().BeNull(); // só o SINAL booleano ComErro
    }

    [Fact]
    public async Task Tenant_ComFalha_SinalizaErro_SemExporTextoInterno()
    {
        var tenant = TenantValido();
        tenant.FalharProvisionamento("stacktrace_interno_sensivel", Momento); // texto interno JAMAIS exibido

        var autz = new AutorizacaoServicoFake();
        var consulta = new ConsultaTenantAutorizada(autz, new ContextoTenantFake(TenantId), new RepositorioTenantFake(tenant));

        var d = await consulta.ObterAtualAsync();

        d.Should().NotBeNull();
        d!.ComErro.Should().BeTrue(); // apenas SINALIZA
        d.Estado.Should().Be(EstadoProvisionamento.Falhado);
        d.Operacional.Should().BeFalse();
    }

    [Fact]
    public async Task Tenant_Autorizado_MasInexistente_RetornaNull()
    {
        var consulta = new ConsultaTenantAutorizada(
            new AutorizacaoServicoFake(), new ContextoTenantFake(TenantId), new RepositorioTenantFake(null));

        (await consulta.ObterAtualAsync()).Should().BeNull();
    }

    // ===== Fábricas de domínio =====

    private static Tenant TenantValido()
    {
        var t = Tenant.Criar(
            Cnpj.Criar("11444777000161"), NomeTenant.Criar("stake"), "Empresa Contribuinte", "cofre://conexao/tenant", Momento, id: TenantId);
        t.IniciarProvisionamento(Momento);
        t.ConcluirProvisionamento("v1", "20260101_Inicial", Momento); // → Pronto (não operacional)
        return t;
    }

    // ===== Dobras (fakes) locais =====

    private sealed class ContextoTenantFake(Guid? tenantId) : ITenantContext
    {
        public EstadoTenant Estado => tenantId is null ? EstadoTenant.NaoResolvido : EstadoTenant.Autorizado;

        public string? PrincipalId => "usuario-teste";

        public Guid? TenantId => tenantId;

        public string? NomeBaseFisica => null; // nunca preenchido nos read-models de leitura

        public string? ReferenciaSegredoConexao => null;

        public bool EstaAutorizado => tenantId is not null;
    }

    private sealed class AutorizacaoServicoFake : IServicoAutorizacao
    {
        public bool Negar { get; init; }

        public int Chamadas { get; private set; }

        public string? Permissao { get; private set; }

        public string? PermissaoAdministrativa { get; private set; }

        public ValueTask ExigirAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
        {
            Chamadas++;
            Permissao = permissao;
            return Negar ? throw new AutorizacaoNegadaException() : ValueTask.CompletedTask;
        }

        public ValueTask ExigirAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
        {
            Chamadas++;
            PermissaoAdministrativa = permissao;
            return Negar ? throw new AutorizacaoNegadaException() : ValueTask.CompletedTask;
        }

        public ValueTask<ResultadoAutorizacao> AvaliarAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Os read-models usam apenas Exigir*.");

        public ValueTask<ResultadoAutorizacao> AvaliarAdministrativoAsync(Guid tenantId, string permissao, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Os read-models usam apenas Exigir*.");
    }

    private sealed class RepositorioTenantFake(Tenant? tenant) : IRepositorioTenant
    {
        public int ChamadasObterPorId { get; private set; }

        public ValueTask<Tenant?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            ChamadasObterPorId++;
            return ValueTask.FromResult(tenant);
        }

        public ValueTask<bool> ExisteComCnpjAsync(Cnpj cnpj, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<bool> ExisteComNomeAsync(NomeTenant nome, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask AdicionarAsync(Tenant tenant, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<Guid>> ListarProvisionaveisAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
