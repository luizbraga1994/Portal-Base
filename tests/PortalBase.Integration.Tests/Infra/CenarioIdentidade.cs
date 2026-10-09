using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Integration.Tests.Infra;

/// <summary>
/// Helpers de cenário para os testes de identidade/autorização (semeadura real no catálogo):
/// tenants PRONTOS (sem exigir base física), usuários com credencial e papéis escopados.
/// </summary>
internal static class CenarioIdentidade
{
    /// <summary>
    /// Cria um tenant já OPERACIONAL (Ativo) no catálogo, sem provisionar a base física — para os
    /// testes de identidade/autorização que exigem um tenant ativo (Prompt 09).
    /// </summary>
    public static async Task<Guid> CriarTenantProntoAsync(FixtureMultiTenant fixture, string cnpj)
    {
        using var escopo = fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        var agora = relogio.AgoraUtc;

        var tenant = Tenant.Criar(Cnpj.Criar(cnpj), NomeTenant.Criar("T" + Guid.NewGuid().ToString("N")[..12]), "Empresa", "cofre://x", agora);
        tenant.IniciarProvisionamento(agora);
        tenant.ConcluirProvisionamento("v1", "v1", agora);
        tenant.Ativar(agora); // Ready → Active: torna o tenant operacional para os cenários de acesso.
        catalogo.Tenants.Add(tenant);
        await catalogo.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>Cria um usuário ATIVO com credencial (hash real) e retorna id e login.</summary>
    public static async Task<(Guid Id, string Login)> CriarUsuarioComSenhaAsync(FixtureMultiTenant fixture, string senha)
    {
        using var escopo = fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<IHasherSenha>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        var agora = relogio.AgoraUtc;

        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var usuario = Usuario.Criar($"u_{sufixo}", "Usuário", $"u_{sufixo}@exemplo.com", agora);
        usuario.DefinirCredencial(hasher.Gerar(senha), agora);
        catalogo.Usuarios.Add(usuario);
        await catalogo.SaveChangesAsync();
        return (usuario.Id, usuario.Login);
    }

    /// <summary>Concede (semeadura direta) um vínculo ativo + papel escopado a um usuário no tenant.</summary>
    public static async Task ConcederPapelAsync(FixtureMultiTenant fixture, Guid usuarioId, Guid tenantId, PapelConhecido papel)
    {
        using var escopo = fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        var nomePapel = papel.ToString();

        var vinculo = await catalogo.UsuarioTenants
            .FirstOrDefaultAsync(v => v.UsuarioId == usuarioId && v.TenantId == tenantId);
        if (vinculo is null)
        {
            vinculo = UsuarioTenant.Conceder(usuarioId, tenantId, relogio.AgoraUtc);
            catalogo.UsuarioTenants.Add(vinculo);
            await catalogo.SaveChangesAsync();
        }

        var papelEntidade = await catalogo.Papeis.FirstAsync(p => p.Nome == nomePapel);
        catalogo.UsuarioTenantPapeis.Add(new UsuarioTenantPapel(vinculo.Id, papelEntidade.Id));
        await catalogo.SaveChangesAsync();
    }

    /// <summary>Revoga o vínculo do usuário com o tenant (perda de acesso).</summary>
    public static async Task RevogarAcessoAsync(FixtureMultiTenant fixture, Guid usuarioId, Guid tenantId)
    {
        using var escopo = fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var relogio = escopo.ServiceProvider.GetRequiredService<IRelogio>();
        var vinculo = await catalogo.UsuarioTenants.FirstAsync(v => v.UsuarioId == usuarioId && v.TenantId == tenantId);
        vinculo.Revogar(relogio.AgoraUtc);
        await catalogo.SaveChangesAsync();
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

    /// <summary>Abre um escopo com o contexto de segurança definido (usuário autenticado + MFA opcional).</summary>
    public static IServiceScope EscopoAutenticado(FixtureMultiTenant fixture, Guid usuarioId, bool mfaSatisfeito)
    {
        var escopo = fixture.CriarEscopo();
        var seguranca = escopo.ServiceProvider.GetRequiredService<IContextoSegurancaMutavel>();
        seguranca.Definir(usuarioId.ToString(), mfaSatisfeito);
        return escopo;
    }
}
