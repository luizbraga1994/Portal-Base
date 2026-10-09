using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Provisionamento;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Provisionamento;

public sealed class ProvisionadorTenantTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static Tenant NovoTenant()
        => Tenant.Criar(Cnpj.Criar("11444777000161"), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa", "cofre://x", Momento);

    private static ProvisionadorTenant Montar(
        Tenant tenant,
        bool lockAdquire,
        AdministradorBancoFake admin,
        MigradorFake migrador,
        out UnitOfWorkFake unitOfWork,
        AuditoriaFake? auditoria = null)
    {
        var repositorio = new RepositorioTenantFake();
        repositorio.Adicionados.Add(tenant);
        unitOfWork = new UnitOfWorkFake();

        return new ProvisionadorTenant(
            repositorio,
            admin,
            migrador,
            new ValidadorProvisionamentoFake(),
            new LockProvisionamentoFake(lockAdquire),
            unitOfWork,
            auditoria ?? new AuditoriaFake(),
            new MetricasProvisionamentoFake(),
            new RelogioFixo(Momento),
            NullLogger<ProvisionadorTenant>.Instance);
    }

    [Fact]
    public async Task Sucesso_CriaBanco_Migra_Concede_E_TornaPronto()
    {
        var tenant = NovoTenant();
        var admin = new AdministradorBancoFake();
        var provisionador = Montar(tenant, lockAdquire: true, admin, new MigradorFake(), out _);

        var resultado = await provisionador.ProvisionarAsync(tenant.Id, "admin-1");

        resultado.Sucesso.Should().BeTrue();
        // Provisionamento termina em PRONTO (Ready): infra validada, mas ainda NÃO operacional
        // (só a ativação separada torna o tenant Ativo/operacional — Prompt 09).
        tenant.Estado.Should().Be(EstadoProvisionamento.Pronto);
        tenant.EstaOperacional.Should().BeFalse();
        tenant.EstaProvisionado.Should().BeTrue();
        admin.CriacoesDeBanco.Should().Be(1);
        admin.Grants.Should().Be(1);
    }

    [Fact]
    public async Task ValidacaoPosProvisionamento_Falha_DeixaTenantFalhado()
    {
        var tenant = NovoTenant();
        var provisionador = new ProvisionadorTenant(
            NovoRepositorioCom(tenant),
            new AdministradorBancoFake(),
            new MigradorFake(),
            new ValidadorProvisionamentoFake { Valido = false }, // infra não passa na validação
            new LockProvisionamentoFake(adquire: true),
            new UnitOfWorkFake(),
            new AuditoriaFake(),
            new MetricasProvisionamentoFake(),
            new RelogioFixo(Momento),
            NullLogger<ProvisionadorTenant>.Instance);

        var resultado = await provisionador.ProvisionarAsync(tenant.Id, "admin-1");

        resultado.Sucesso.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoProvisionamento.Falhado); // nunca Ready parcial
    }

    private static RepositorioTenantFake NovoRepositorioCom(Tenant tenant)
    {
        var repositorio = new RepositorioTenantFake();
        repositorio.Adicionados.Add(tenant);
        return repositorio;
    }

    [Fact]
    public async Task LockNaoAdquirido_NaoInicia_Provisionamento()
    {
        var tenant = NovoTenant();
        var admin = new AdministradorBancoFake();
        var provisionador = Montar(tenant, lockAdquire: false, admin, new MigradorFake(), out _);

        var resultado = await provisionador.ProvisionarAsync(tenant.Id, "admin-1");

        resultado.Estado.Should().Be(EstadoProvisionamento.Provisionando);
        admin.CriacoesDeBanco.Should().Be(0);
        tenant.Estado.Should().Be(EstadoProvisionamento.Pendente);
    }

    [Fact]
    public async Task FalhaNaMigration_DeixaTenantFalhado_E_NaoOperacional()
    {
        var tenant = NovoTenant();
        var migrador = new MigradorFake { FalharMigracao = true };
        var provisionador = Montar(tenant, lockAdquire: true, new AdministradorBancoFake(), migrador, out _);

        var resultado = await provisionador.ProvisionarAsync(tenant.Id, "admin-1");

        resultado.Sucesso.Should().BeFalse();
        resultado.Erro.Should().NotBeNullOrEmpty();
        tenant.Estado.Should().Be(EstadoProvisionamento.Falhado);
        tenant.EstaOperacional.Should().BeFalse();
    }

    [Fact]
    public async Task TenantDesabilitado_NaoProvisiona()
    {
        var tenant = NovoTenant();
        tenant.Desabilitar(Momento);
        var admin = new AdministradorBancoFake();
        var provisionador = Montar(tenant, lockAdquire: true, admin, new MigradorFake(), out _);

        var resultado = await provisionador.ProvisionarAsync(tenant.Id, "admin-1");

        resultado.Estado.Should().Be(EstadoProvisionamento.Desabilitado);
        admin.CriacoesDeBanco.Should().Be(0);
    }

    [Fact]
    public async Task TenantAtivo_AtualizaSchema_E_PermaneceOperacional()
    {
        var tenant = NovoTenant();
        tenant.IniciarProvisionamento(Momento);
        tenant.ConcluirProvisionamento("v0", "v0", Momento);
        tenant.Ativar(Momento); // tenant operacional (Ativo)
        var provisionador = Montar(tenant, lockAdquire: true, new AdministradorBancoFake { BancoExiste = true }, new MigradorFake(), out _);

        var resultado = await provisionador.ProvisionarAsync(tenant.Id, "admin-1");

        // Atualização de schema em tenant Ativo preserva o estado operacional.
        resultado.Estado.Should().Be(EstadoProvisionamento.Ativo);
        tenant.EstaOperacional.Should().BeTrue();
        tenant.SchemaVersion.Should().Be("20260903_TenantBaseInicial");
    }

    // ---------------- base já existente (nome escolhido) ----------------

    [Fact]
    public async Task Base_Existente_De_Outro_Contribuinte_E_Recusada_Sem_Migrar_Nem_Conceder()
    {
        var tenant = NovoTenant();
        var admin = new AdministradorBancoFake
        {
            BancoExiste = true,
            Proprietario = new ProprietarioBaseTenant(false, Guid.NewGuid(), "11222333000181"), // outro tenant, outro CNPJ
        };
        var migrador = new MigradorFake();
        var auditoria = new AuditoriaFake();
        var provisionador = Montar(tenant, lockAdquire: true, admin, migrador, out _, auditoria);

        var resultado = await provisionador.ProvisionarAsync(tenant.Id, "admin-1");

        resultado.Sucesso.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoProvisionamento.Falhado);
        resultado.Erro.Should().Contain("outro contribuinte");
        migrador.Migracoes.Should().Be(0, "nada é aplicado sobre dados de outro contribuinte");
        admin.Grants.Should().Be(0);
        admin.RegistrosDeProprietario.Should().Be(0, "a marca do dono verdadeiro não é sobrescrita");
        auditoria.Eventos.Should().Contain(e => e.Operacao == AcaoAuditoria.TenantDatabaseOwnershipConflict
            && e.Resultado == ResultadoAuditoria.Negado);
    }

    [Fact]
    public async Task Base_Existente_Com_Dados_E_Sem_Dono_Conhecido_E_Recusada()
    {
        var tenant = NovoTenant();
        var admin = new AdministradorBancoFake { BancoExiste = true, Proprietario = new ProprietarioBaseTenant(false, null, null) };
        var migrador = new MigradorFake();
        var provisionador = Montar(tenant, lockAdquire: true, admin, migrador, out _);

        var resultado = await provisionador.ProvisionarAsync(tenant.Id, "admin-1");

        tenant.Estado.Should().Be(EstadoProvisionamento.Falhado);
        resultado.Sucesso.Should().BeFalse();
        migrador.Migracoes.Should().Be(0);
    }

    public static TheoryData<string> DonosAceitos => new() { "vazia", "mesmo-cnpj", "proprio-tenant" };

    [Theory]
    [MemberData(nameof(DonosAceitos))]
    public async Task Base_Existente_Vazia_Do_Mesmo_Cnpj_Ou_Do_Proprio_Tenant_E_Reaproveitada(string caso)
    {
        var tenant = NovoTenant();
        var admin = new AdministradorBancoFake
        {
            BancoExiste = true,
            Proprietario = caso switch
            {
                "vazia" => ProprietarioBaseTenant.BaseVazia,
                "mesmo-cnpj" => new ProprietarioBaseTenant(false, Guid.NewGuid(), tenant.Cnpj.Valor), // recadastro do mesmo contribuinte
                _ => new ProprietarioBaseTenant(false, tenant.Id, tenant.Cnpj.Valor), // nova tentativa
            },
        };
        var auditoria = new AuditoriaFake();
        var provisionador = Montar(tenant, lockAdquire: true, admin, new MigradorFake(), out _, auditoria);

        var resultado = await provisionador.ProvisionarAsync(tenant.Id, "admin-1");

        resultado.Sucesso.Should().BeTrue();
        admin.CriacoesDeBanco.Should().Be(0);
        admin.Proprietario.TenantId.Should().Be(tenant.Id, "a base passa a ser marcada como deste tenant");
        auditoria.Eventos.Count(e => e.Operacao == AcaoAuditoria.TenantDatabaseReused)
            .Should().Be(caso == "proprio-tenant" ? 0 : 1, "reaproveitar base que não era deste tenant é auditado");
    }

    [Fact]
    public async Task Base_Nova_Recebe_A_Marca_Do_Proprietario_Antes_Das_Migrations()
    {
        var tenant = NovoTenant();
        var admin = new AdministradorBancoFake();
        var migrador = new MigradorFake { FalharMigracao = true };
        var provisionador = Montar(tenant, lockAdquire: true, admin, migrador, out _);

        (await provisionador.ProvisionarAsync(tenant.Id, "admin-1")).Sucesso.Should().BeFalse();

        // Mesmo com a migration falhando, a base já é reconhecida como deste tenant: a nova tentativa a reaproveita.
        admin.Proprietario.Should().Be(new ProprietarioBaseTenant(false, tenant.Id, tenant.Cnpj.Valor));
        migrador.FalharMigracao = false;
        (await provisionador.ProvisionarAsync(tenant.Id, "admin-1")).Sucesso.Should().BeTrue();
    }

    [Fact]
    public async Task Atualizacao_De_Schema_Nao_Consulta_Nem_Regrava_O_Dono()
    {
        var tenant = NovoTenant();
        tenant.IniciarProvisionamento(Momento);
        tenant.ConcluirProvisionamento("v0", "v0", Momento);
        var admin = new AdministradorBancoFake { BancoExiste = true, Proprietario = new ProprietarioBaseTenant(false, null, null) };
        var provisionador = Montar(tenant, lockAdquire: true, admin, new MigradorFake(), out _);

        (await provisionador.ProvisionarAsync(tenant.Id, "admin-1")).Sucesso.Should().BeTrue("bases legadas (sem marca) continuam atualizáveis");
        admin.RegistrosDeProprietario.Should().Be(0);
    }

    [Fact]
    public void Base_Legada_Sem_Marca_So_E_Aceita_Pelo_Proprio_Tenant()
    {
        var id = Guid.NewGuid();
        var cnpj = Cnpj.Criar("11444777000161");
        var semDono = new ProprietarioBaseTenant(false, null, null);

        // tenant_<id> é derivado do próprio Id (colisão impossível); qualquer outro nome com dados e sem dono é recusado.
        ReaproveitamentoBaseTenant.Permitido(semDono, id, cnpj, "tenant_" + id.ToString("N")).Should().BeTrue();
        ReaproveitamentoBaseTenant.Permitido(semDono, Guid.NewGuid(), cnpj, "tenant_" + id.ToString("N")).Should().BeFalse();
        ReaproveitamentoBaseTenant.Permitido(semDono, id, cnpj, "portalbase_tenant_stake").Should().BeFalse();
        ReaproveitamentoBaseTenant.Permitido(semDono, null, cnpj, "portalbase_tenant_stake").Should().BeFalse();
        ReaproveitamentoBaseTenant.Permitido(ProprietarioBaseTenant.BaseVazia, null, cnpj, "portalbase_tenant_stake").Should().BeTrue();
    }
}
