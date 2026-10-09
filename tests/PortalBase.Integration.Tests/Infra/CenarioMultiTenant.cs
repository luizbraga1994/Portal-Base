using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Provisionamento;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Persistencia.BaseTenant;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Integration.Tests.Infra;

/// <summary>Helpers de cenário para os testes de isolamento (semeadura + provisionamento reais).</summary>
internal static class CenarioMultiTenant
{
    private static readonly int[] PesosPrimeiro = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
    private static readonly int[] PesosSegundo = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    /// <summary>
    /// Gera um CNPJ VÁLIDO (14 dígitos com DV corretos) e único por chamada. O catálogo é
    /// compartilhado entre os testes da coleção e impõe unicidade de CNPJ (ux_tenant_cnpj);
    /// gerar um CNPJ novo por tenant evita colisão entre testes que rodam no mesmo run.
    /// </summary>
    public static string GerarCnpjValido()
    {
        var digitos = new int[14];
        for (var i = 0; i < 12; i++)
        {
            digitos[i] = Random.Shared.Next(0, 10);
        }

        // Evita a sequência de dígitos todos iguais (rejeitada pela validação de domínio).
        if (digitos.Take(12).Distinct().Count() == 1)
        {
            digitos[0] = (digitos[0] + 1) % 10;
        }

        digitos[12] = CalcularDigito(digitos, PesosPrimeiro);
        digitos[13] = CalcularDigito(digitos, PesosSegundo);
        return string.Concat(digitos.Select(d => (char)('0' + d)));
    }

    private static int CalcularDigito(int[] digitos, int[] pesos)
    {
        var soma = 0;
        for (var i = 0; i < pesos.Length; i++)
        {
            soma += digitos[i] * pesos[i];
        }

        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }

    /// <summary>Cria um tenant (Pendente), salva e provisiona (cria base, migra, concede grants).</summary>
    public static async Task<Guid> ProvisionarTenantAsync(FixtureMultiTenant fixture, string cnpj)
    {
        Guid id;
        using (var escopo = fixture.CriarEscopo())
        {
            var repositorio = escopo.ServiceProvider.GetRequiredService<IRepositorioTenant>();
            var unitOfWork = escopo.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var tenant = Tenant.Criar(Cnpj.Criar(cnpj), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa", "cofre://x", DateTimeOffset.UtcNow);
            id = tenant.Id;
            await repositorio.AdicionarAsync(tenant);
            await unitOfWork.SalvarAlteracoesAsync();
        }

        fixture.RegistrarTenant(id);

        using (var escopo = fixture.CriarEscopo())
        {
            var provisionador = escopo.ServiceProvider.GetRequiredService<IProvisionadorTenant>();
            var resultado = await provisionador.ProvisionarAsync(id, "admin-sistema");
            if (!resultado.Sucesso)
            {
                throw new InvalidOperationException($"Falha ao provisionar tenant: {resultado.Erro}");
            }
        }

        // Prompt 09: provisionamento termina em Pronto (Ready). A ativação (Ready→Active) é o
        // passo que torna o tenant operacional — os cenários de acesso exigem tenant Ativo.
        await AtivarTenantAsync(fixture, id);
        return id;
    }

    /// <summary>Ativa (Ready→Active) um tenant diretamente no catálogo (setup de cenário).</summary>
    public static async Task AtivarTenantAsync(FixtureMultiTenant fixture, Guid tenantId)
    {
        using var escopo = fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        var tenant = await catalogo.Tenants.FirstAsync(t => t.Id == tenantId);
        tenant.Ativar(relogio.AgoraUtc);
        await catalogo.SaveChangesAsync();
    }

    /// <summary>Cria um usuário ATIVO no catálogo e retorna seu id (string).</summary>
    public static async Task<string> CriarUsuarioAsync(FixtureMultiTenant fixture)
    {
        using var escopo = fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var usuario = Usuario.Criar($"u_{sufixo}", "Usuário Teste", $"u_{sufixo}@exemplo.com", DateTimeOffset.UtcNow);
        catalogo.Usuarios.Add(usuario);
        await catalogo.SaveChangesAsync();
        return usuario.Id.ToString();
    }

    /// <summary>Concede a um usuário existente vínculo ATIVO com um tenant.</summary>
    public static async Task VincularAsync(FixtureMultiTenant fixture, string usuarioId, Guid tenantId)
    {
        using var escopo = fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        catalogo.UsuarioTenants.Add(UsuarioTenant.Conceder(Guid.Parse(usuarioId), tenantId, DateTimeOffset.UtcNow));
        await catalogo.SaveChangesAsync();
    }

    /// <summary>Resolve+autoriza o tenant e abre o contexto contábil (fail-closed) no escopo dado.</summary>
    public static async Task<PortalBaseTenantDbContext> AbrirContabilAsync(IServiceScope escopo, string usuarioId, Guid tenantId)
    {
        var resolvedor = escopo.ServiceProvider.GetRequiredService<IResolvedorTenant>();
        var contexto = await resolvedor.ResolverAsync(usuarioId, tenantId);
        var fabrica = escopo.ServiceProvider.GetRequiredService<IFabricaContextoTenant>();
        return await fabrica.CriarAsync(contexto);
    }

    /// <summary>Desabilita um tenant no catálogo.</summary>
    public static async Task DesabilitarTenantAsync(FixtureMultiTenant fixture, Guid tenantId)
    {
        using var escopo = fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        var tenant = await catalogo.Tenants.FirstAsync(t => t.Id == tenantId);
        tenant.Desabilitar(relogio.AgoraUtc);
        await catalogo.SaveChangesAsync();
    }
}
