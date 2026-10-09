using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Provisionamento;
using PortalBase.Domain.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// Ciclo de vida OPERACIONAL contra MySQL real (Prompt 09): Ready→Active→Disabled→Active, com
/// resolução/autorização reais provando que só um tenant ATIVO é operacional. A ativação exige
/// autorização administrativa (papel Administrador → <c>admin.tenant</c>). Pulado sem
/// <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class CicloVidaTenantMySqlTests
{
    private readonly FixtureMultiTenant _fixture;

    public CicloVidaTenantMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Lifecycle_Completo_So_Ativo_E_Operacional()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var tenantId = await ProvisionarAteProntoAsync(CenarioMultiTenant.GerarCnpjValido());
        var (usuarioId, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaF0rte!");
        // Papel Administrador concede admin.tenant + cria o vínculo ativo com o tenant.
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuarioId, tenantId, PapelConhecido.Administrador);
        var usuario = usuarioId.ToString();

        // PRONTO (Ready): infra validada, porém NÃO operacional → resolução negada.
        await ResolucaoDeve(usuario, tenantId, autorizado: false);

        // Ativação (Ready → Active), autorizada administrativamente.
        (await ExecutarComoAsync(usuarioId, c => c.AtivarAsync(tenantId))).Sucesso.Should().BeTrue();
        (await EstadoAsync(tenantId)).Should().Be(EstadoProvisionamento.Ativo);

        // ATIVO → resolução autorizada (operacional).
        await ResolucaoDeve(usuario, tenantId, autorizado: true);

        // Desabilitação → bloqueado.
        (await ExecutarComoAsync(usuarioId, c => c.DesabilitarAsync(tenantId))).Sucesso.Should().BeTrue();
        (await EstadoAsync(tenantId)).Should().Be(EstadoProvisionamento.Desabilitado);
        await ResolucaoDeve(usuario, tenantId, autorizado: false);

        // Reativação (Disabled → Active), revalidando a infraestrutura.
        (await ExecutarComoAsync(usuarioId, c => c.ReativarAsync(tenantId))).Sucesso.Should().BeTrue();
        (await EstadoAsync(tenantId)).Should().Be(EstadoProvisionamento.Ativo);
        await ResolucaoDeve(usuario, tenantId, autorizado: true);
    }

    [SkippableFact]
    public async Task Ativacao_Sem_Permissao_E_Negada()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var tenantId = await ProvisionarAteProntoAsync(CenarioMultiTenant.GerarCnpjValido());
        var (usuarioId, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaF0rte!");
        // Apenas Leitor: possui vínculo, mas NÃO tem admin.tenant.
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuarioId, tenantId, PapelConhecido.Leitor);

        var acao = async () => await ExecutarComoAsync(usuarioId, c => c.AtivarAsync(tenantId));

        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        (await EstadoAsync(tenantId)).Should().Be(EstadoProvisionamento.Pronto); // permanece Ready
    }

    [SkippableFact]
    public async Task Ativacao_Concorrente_Deixa_Estado_Consistente()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        var tenantId = await ProvisionarAteProntoAsync(CenarioMultiTenant.GerarCnpjValido());
        var (usuarioId, _) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "SenhaF0rte!");
        await CenarioIdentidade.ConcederPapelAsync(_fixture, usuarioId, tenantId, PapelConhecido.Administrador);

        async Task Ativar()
        {
            try
            {
                await ExecutarComoAsync(usuarioId, c => c.AtivarAsync(tenantId));
            }
            catch (Exception excecao) when (excecao is not Xunit.Sdk.XunitException)
            {
                // Concorrência otimista pode fazer um dos dois falhar — aceitável (sem corromper).
            }
        }

        await Task.WhenAll(Ativar(), Ativar());

        (await EstadoAsync(tenantId)).Should().Be(EstadoProvisionamento.Ativo); // estado final consistente
    }

    // Cria um tenant pendente e provisiona SEM ativar (permanece Pronto/Ready).
    [SkippableFact]
    public async Task Provisionamento_Nao_Herda_Base_Existente_De_Outro_Contribuinte()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");

        // Tenant com nome escolhido cuja base JÁ existe, marcada como de OUTRO contribuinte (ex.: sobra de catálogo perdido).
        var nome = NomeTenant.Criar("conflito_" + Guid.NewGuid().ToString("N")[..10]);
        var outroTenant = Guid.NewGuid();
        var outroCnpj = CenarioMultiTenant.GerarCnpjValido();
        Guid id;
        using (var escopo = _fixture.CriarEscopo())
        {
            var admin = escopo.ServiceProvider.GetRequiredService<IAdministradorBancoTenant>();
            await admin.CriarBancoSeAusenteAsync(nome.NomeBaseFisica);
            await admin.RegistrarProprietarioAsync(nome.NomeBaseFisica, outroTenant, outroCnpj);

            var tenant = Tenant.Criar(Cnpj.Criar(CenarioMultiTenant.GerarCnpjValido()), nome, "Empresa", "cofre://x", DateTimeOffset.UtcNow);
            id = tenant.Id;
            await escopo.ServiceProvider.GetRequiredService<IRepositorioTenant>().AdicionarAsync(tenant);
            await escopo.ServiceProvider.GetRequiredService<IUnitOfWork>().SalvarAlteracoesAsync();
        }

        _fixture.RegistrarTenant(id); // a limpeza remove a base pelo nome gravado no catálogo

        using (var escopo = _fixture.CriarEscopo())
        {
            var resultado = await escopo.ServiceProvider.GetRequiredService<IProvisionadorTenant>().ProvisionarAsync(id, "sistema");

            resultado.Sucesso.Should().BeFalse();
            resultado.Estado.Should().Be(EstadoProvisionamento.Falhado);
            resultado.Erro.Should().Contain("outro contribuinte").And.NotContain("password", "o erro é sanitizado");

            // A base do outro contribuinte não foi tocada: a marca continua dele e nenhuma migration foi aplicada.
            var admin = escopo.ServiceProvider.GetRequiredService<IAdministradorBancoTenant>();
            (await admin.ObterProprietarioAsync(nome.NomeBaseFisica)).Should().Be(new ProprietarioBaseTenant(false, outroTenant, outroCnpj));
            (await escopo.ServiceProvider.GetRequiredService<IValidadorProvisionamento>().ValidarAsync(nome.NomeBaseFisica)).Ok
                .Should().BeFalse("o schema contábil não foi aplicado sobre a base alheia");
        }

        (await EstadoAsync(id)).Should().Be(EstadoProvisionamento.Falhado, "sem base própria o tenant não fica Pronto");
    }

    private async Task<Guid> ProvisionarAteProntoAsync(string cnpj)
    {
        Guid id;
        using (var escopo = _fixture.CriarEscopo())
        {
            var repo = escopo.ServiceProvider.GetRequiredService<IRepositorioTenant>();
            var uow = escopo.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var tenant = Tenant.Criar(Cnpj.Criar(cnpj), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa", "cofre://x", DateTimeOffset.UtcNow);
            id = tenant.Id;
            await repo.AdicionarAsync(tenant);
            await uow.SalvarAlteracoesAsync();
        }

        _fixture.RegistrarTenant(id);

        using (var escopo = _fixture.CriarEscopo())
        {
            var provisionador = escopo.ServiceProvider.GetRequiredService<IProvisionadorTenant>();
            var resultado = await provisionador.ProvisionarAsync(id, "sistema");
            resultado.Estado.Should().Be(EstadoProvisionamento.Pronto); // Ready validado, não ativado
        }

        return id;
    }

    // Executa uma operação de ciclo de vida num escopo AUTENTICADO como o usuário (contexto de
    // segurança definido), descartando o escopo ao final.
    private async Task<ResultadoCicloVida> ExecutarComoAsync(Guid usuarioId, Func<CicloVidaTenant, Task<ResultadoCicloVida>> operacao)
    {
        using var escopo = CenarioIdentidade.EscopoAutenticado(_fixture, usuarioId, mfaSatisfeito: false);
        var ciclo = escopo.ServiceProvider.GetRequiredService<CicloVidaTenant>();
        return await operacao(ciclo);
    }

    private async Task<EstadoProvisionamento> EstadoAsync(Guid tenantId)
    {
        using var escopo = _fixture.CriarEscopo();
        var repo = escopo.ServiceProvider.GetRequiredService<IRepositorioTenant>();
        return (await repo.ObterPorIdAsync(tenantId))!.Estado;
    }

    private async Task ResolucaoDeve(string usuario, Guid tenantId, bool autorizado)
    {
        using var escopo = _fixture.CriarEscopo();
        var resolvedor = escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>();
        var acao = async () => await resolvedor.ResolverAsync(usuario, tenantId);

        if (autorizado)
        {
            var contexto = await resolvedor.ResolverAsync(usuario, tenantId);
            contexto.EstaAutorizado.Should().BeTrue();
        }
        else
        {
            await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
        }
    }
}
