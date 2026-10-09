using FluentAssertions;
using Microsoft.Extensions.Options;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Segredos;
using PortalBase.Infrastructure.Opcoes;
using PortalBase.Infrastructure.Segredos;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Acesso a segredo por tenant (Prompt 06, seção 24): o nome é derivado do tenant no servidor
/// (o cliente não escolhe a referência), a permissão é validada e o acesso é auditado sem valor.
/// </summary>
public sealed class AcessoSegredoTenantTests
{
    private const string Ambiente = "test";
    private static readonly DateTimeOffset Agora = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private readonly ServicoAutorizacaoFake _autz = new();
    private readonly ProvedorSegredosFake _segredos = new();
    private readonly ContextoSegurancaFake _seguranca = new();
    private readonly AuditoriaColetora _auditoria = new();
    private readonly AcessoSegredoTenant _acesso;

    public AcessoSegredoTenantTests()
        => _acesso = new AcessoSegredoTenant(
            _autz, _segredos, _segredos, _seguranca, _auditoria, new RelogioFixo(Agora),
            Options.Create(new OpcoesVault { Ambiente = Ambiente }));

    [Fact] // acesso autorizado usa o nome derivado do tenant e audita (sem valor)
    public async Task Autorizado_Obtem_Do_Nome_Do_Tenant_E_Audita()
    {
        var nome = NomeSegredo.SegredoIntegracao(Ambiente, TenantA);
        _segredos.Valores[nome] = "segredo-integracao-a";
        _autz.Permitidos.Add((TenantA, Permissoes.AdminTenant));

        var valor = await _acesso.ObterAsync(TenantA, TipoSegredoTenant.SegredoIntegracao);

        valor.Should().Be("segredo-integracao-a");
        var evento = _auditoria.Eventos.Should().ContainSingle(e => e.Operacao == AcaoAuditoria.SecretAccessed).Which;
        evento.RecursoId.Should().Be(nome); // nome lógico, NUNCA o valor
        evento.RecursoId.Should().NotBe("segredo-integracao-a");
    }

    [Fact] // §24 — sem permissão: negado antes de tocar o cofre
    public async Task Sem_Permissao_E_Negado()
    {
        _segredos.Valores[NomeSegredo.SegredoIntegracao(Ambiente, TenantA)] = "segredo-integracao-a";

        var acao = async () => await _acesso.ObterAsync(TenantA, TipoSegredoTenant.SegredoIntegracao);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        _segredos.Chamadas.Should().Be(0);
    }

    [Fact] // §24 — autorizado só para A não alcança o segredo de B (nome + autorização por tenant)
    public async Task Autorizado_Para_A_Nao_Alcança_B()
    {
        _segredos.Valores[NomeSegredo.SegredoIntegracao(Ambiente, TenantB)] = "segredo-integracao-b";
        _autz.Permitidos.Add((TenantA, Permissoes.AdminTenant)); // só A

        var acao = async () => await _acesso.ObterAsync(TenantB, TipoSegredoTenant.SegredoIntegracao);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }
}
