using FluentAssertions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Infrastructure.Opcoes;
using PortalBase.Infrastructure.Seguranca;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// Autorização de PLATAFORMA (Prompt 10.1) — segurança do catálogo GLOBAL. Deny-by-default: só
/// administradores de plataforma explicitamente listados, com MFA quando exigido, podem administrar a
/// plataforma. NUNCA deriva a decisão de tenant/cliente. Um usuário de tenant (não listado)
/// jamais é autorizado (regras 3, 4, 17). Sem banco.
/// </summary>
public sealed class AutorizacaoPlataformaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private const string Admin = "11111111-1111-1111-1111-111111111111";
    private const string Tenant = "22222222-2222-2222-2222-222222222222";

    private sealed class SegurancaFake : IContextoSeguranca
    {
        public bool EstaAutenticado { get; set; } = true;
        public string? PrincipalId { get; set; }
        public bool MfaSatisfeito { get; set; } = true;
    }

    private sealed class AuditoriaFake : IAuditoria
    {
        public int Negacoes { get; private set; }
        public ValueTask RegistrarAsync(EventoAuditoria evento, CancellationToken cancellationToken = default)
        {
            if (evento.Resultado == ResultadoAuditoria.Negado) { Negacoes++; }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RelogioFixo(DateTimeOffset agora) : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; } = agora;
    }

    private static AutorizacaoPlataforma Criar(SegurancaFake seguranca, AuditoriaFake auditoria, bool exigirMfa = true, params string[] admins)
        => Criar(seguranca, auditoria, new RepositorioUsuarioPlataformaFake(), exigirMfa, admins);

    private static AutorizacaoPlataforma Criar(
        SegurancaFake seguranca, AuditoriaFake auditoria, RepositorioUsuarioPlataformaFake usuarios, bool exigirMfa, params string[] admins)
    {
        var opcoes = new OpcoesAutorizacaoPlataforma { ExigirMfa = exigirMfa };
        foreach (var a in admins) { opcoes.AdministradoresPlataforma.Add(a); }
        return new AutorizacaoPlataforma(seguranca, usuarios, opcoes, auditoria, new RelogioFixo(T0));
    }

    [Fact]
    public async Task PerfilAdministradorDaPlataforma_NoCatalogo_EhAutorizado_SemMfa_E_SemLista()
    {
        // Perfil gravado no catálogo: acesso total a TODAS as permissões de plataforma, sem segundo fator e sem
        // depender da lista da configuração.
        var usuarios = new RepositorioUsuarioPlataformaFake();
        usuarios.AdministradoresPlataforma.Add(Guid.Parse(Tenant));
        var seguranca = new SegurancaFake { PrincipalId = Tenant, MfaSatisfeito = false };
        var authz = Criar(seguranca, new AuditoriaFake(), usuarios, exigirMfa: true);

        foreach (var permissao in Permissoes.Plataforma)
        {
            (await authz.AvaliarAsync(permissao)).Permitido.Should().BeTrue(permissao);
            await authz.ExigirAsync(permissao); // não lança
        }
    }

    [Fact]
    public async Task SemPerfil_NoCatalogo_ListaDaConfiguracao_ContinuaExigindoMfa()
    {
        var usuarios = new RepositorioUsuarioPlataformaFake(); // ninguém com o perfil no catálogo
        var seguranca = new SegurancaFake { PrincipalId = Admin, MfaSatisfeito = false };
        var authz = Criar(seguranca, new AuditoriaFake(), usuarios, exigirMfa: true, Admin);

        (await authz.AvaliarAsync(Permissoes.BackupExecutar)).Motivo.Should().Be(MotivoNegacao.MfaRequerido);
    }

    [Fact]
    public async Task PerfilAdministradorDaPlataforma_ExigeSessaoAutenticada()
    {
        var usuarios = new RepositorioUsuarioPlataformaFake();
        usuarios.AdministradoresPlataforma.Add(Guid.Parse(Tenant));
        var seguranca = new SegurancaFake { EstaAutenticado = false, PrincipalId = Tenant };
        var authz = Criar(seguranca, new AuditoriaFake(), usuarios, exigirMfa: false);

        (await authz.AvaliarAsync(Permissoes.BackupExecutar)).Motivo.Should().Be(MotivoNegacao.NaoAutenticado);
    }

    [Fact]
    public async Task AdministradorListado_ComMfa_EhAutorizado()
    {
        var seguranca = new SegurancaFake { PrincipalId = Admin, MfaSatisfeito = true };
        var authz = Criar(seguranca, new AuditoriaFake(), exigirMfa: true, Admin);

        (await authz.AvaliarAsync(Permissoes.PlataformaTenantProvisionar)).Permitido.Should().BeTrue();
        await authz.ExigirAsync(Permissoes.PlataformaTenantProvisionar); // não lança
    }

    [Fact]
    public async Task UsuarioDeTenant_NaoListado_EhNegado_E_Auditado()
    {
        var seguranca = new SegurancaFake { PrincipalId = Tenant, MfaSatisfeito = true };
        var auditoria = new AuditoriaFake();
        var authz = Criar(seguranca, auditoria, exigirMfa: true, Admin);

        (await authz.AvaliarAsync(Permissoes.PlataformaTenantProvisionar)).Motivo.Should().Be(MotivoNegacao.SemPermissao);
        var acao = async () => await authz.ExigirAsync(Permissoes.PlataformaTenantProvisionar);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        auditoria.Negacoes.Should().Be(1);
    }

    [Fact]
    public async Task SemAdministradoresConfigurados_NegaTodos_DenyByDefault()
    {
        var seguranca = new SegurancaFake { PrincipalId = Admin, MfaSatisfeito = true };
        var authz = Criar(seguranca, new AuditoriaFake(), exigirMfa: true); // allowlist vazia

        (await authz.AvaliarAsync(Permissoes.PlataformaTenantProvisionar)).Permitido.Should().BeFalse();
    }

    [Fact]
    public async Task NaoAutenticado_EhNegado()
    {
        var seguranca = new SegurancaFake { EstaAutenticado = false, PrincipalId = null };
        var authz = Criar(seguranca, new AuditoriaFake(), exigirMfa: true, Admin);

        (await authz.AvaliarAsync(Permissoes.PlataformaTenantProvisionar)).Motivo.Should().Be(MotivoNegacao.NaoAutenticado);
    }

    [Fact]
    public async Task AdministradorSemMfa_EhNegado_QuandoMfaExigido()
    {
        var seguranca = new SegurancaFake { PrincipalId = Admin, MfaSatisfeito = false };
        var authz = Criar(seguranca, new AuditoriaFake(), exigirMfa: true, Admin);

        (await authz.AvaliarAsync(Permissoes.PlataformaTenantProvisionar)).Motivo.Should().Be(MotivoNegacao.MfaRequerido);
        var acao = async () => await authz.ExigirAsync(Permissoes.PlataformaTenantProvisionar);
        await acao.Should().ThrowAsync<MfaRequeridoException>();
    }

    [Fact]
    public async Task PrincipalDoBackupAgendado_SoPodeExecutarBackup()
    {
        // Principal de sistema definido só pelo Worker: permissão fixa e mínima (backup), sem MFA e sem lista; tudo o
        // mais negado e auditado. Um usuário NÃO vira esse principal (usuários são GUIDs vindos da sessão).
        var seguranca = new SegurancaFake { PrincipalId = PortalBase.Application.Backup.ExecucaoBackupAgendado.PrincipalSistema, MfaSatisfeito = false };
        var auditoria = new AuditoriaFake();
        var authz = Criar(seguranca, auditoria, exigirMfa: true);

        (await authz.AvaliarAsync(Permissoes.BackupExecutar)).Permitido.Should().BeTrue();
        foreach (var permissao in Permissoes.Plataforma.Where(p => p != Permissoes.BackupExecutar))
        {
            (await authz.AvaliarAsync(permissao)).Motivo.Should().Be(MotivoNegacao.SemPermissao, permissao);
        }

        var acao = async () => await authz.ExigirAsync(Permissoes.PlataformaTenantProvisionar);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
        auditoria.Negacoes.Should().Be(1);
    }

    [Fact]
    public async Task OutroPrincipalDeSistema_OuVariacaoDoNome_NaoHerdaAPermissao()
    {
        foreach (var principal in new[] { "sistema:outbox-worker", "SISTEMA:BACKUP-WORKER", "sistema:backup-worker ", "sistema:backup" })
        {
            var authz = Criar(new SegurancaFake { PrincipalId = principal }, new AuditoriaFake(), exigirMfa: false);
            (await authz.AvaliarAsync(Permissoes.BackupExecutar)).Permitido.Should().BeFalse(principal);
        }
    }
}
