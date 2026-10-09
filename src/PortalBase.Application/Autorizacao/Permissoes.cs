using System.Collections.Frozen;

namespace PortalBase.Application.Autorizacao;

/// <summary>
/// Chaves de permissão CENTRALIZADAS (seção 11): evita strings espalhadas pelo código.
/// As chaves são idênticas às persistidas no catálogo (seed). A decisão de autorização é
/// sempre no servidor a partir destas chaves — nunca do nome da tela ou da presença de um
/// botão (seções 9, 42).
/// </summary>
public static class Permissoes
{
    // Dados / operação (exemplo genérico — adapte ao domínio do seu portal)
    public const string DadosVisualizar = "dados.visualizar";
    public const string DadosCadastrar = "dados.cadastrar";
    public const string DadosEditar = "dados.editar";
    public const string DadosExcluir = "dados.excluir";
    public const string DadosImportar = "dados.importar";

    // Administração
    public const string AdminTenant = "admin.tenant";
    public const string AdminUsuarios = "admin.usuarios";

    // Auditoria
    public const string AuditoriaVisualizar = "auditoria.visualizar";

    // Backup / Restore / DR (Prompt 50) — operações de PLATAFORMA altamente privilegiadas (nunca do
    // usuário comum do tenant). O backup roda como job administrativo; o RESTORE é o mais crítico e
    // exige administrador de plataforma + segundo fator (via IAutorizacaoPlataforma; nunca por tenant).
    public const string BackupExecutar = "plataforma.backup.executar";
    public const string RestauracaoExecutar = "plataforma.restore.executar";

    // Governança de segurança / incidentes (Prompt 30) — operação GLOBAL de plataforma (não tenant-escopada).
    public const string SegurancaIncidentesGerir = "seguranca.incidentes.gerir";

    // Provisionamento/criação de tenant (Prompt 71, P2-14) — operação de PLATAFORMA: criar um tenant não
    // tem contexto de tenant ainda (o Id nasce no caso de uso), portanto NUNCA é governada por admin.tenant
    // (tenant-escopada), e sim por IAutorizacaoPlataforma (allowlist de administradores de plataforma).
    public const string PlataformaTenantProvisionar = "plataforma.tenant.provisionar";

    // Marca visual GLOBAL do portal (logo padrão exibida no login e como fallback). É permissão de
    // PLATAFORMA (não de tenant): um administrador de contribuinte NUNCA altera a marca global. A logo
    // POR contribuinte é governada por <see cref="AdminTenant"/> (mesma chave da configuração do tenant).
    public const string MarcaPlataformaAdministrar = "plataforma.marca.administrar";

    // Conceder/remover o perfil "Administrador da plataforma" de um usuário — permissão de PLATAFORMA: só quem já é
    // administrador da plataforma governa quem mais é (nunca a si mesmo).
    public const string PlataformaAdministradoresGerir = "plataforma.administradores.gerir";

    // Configuração do e-mail de disparo (SMTP) pela tela — permissão de PLATAFORMA: o e-mail é um só para o portal todo.
    public const string PlataformaEmailAdministrar = "plataforma.email.administrar";

    /// <summary>
    /// Permissões de ALTO IMPACTO que exigem segundo fator satisfeito na sessão (step-up —
    /// seções 18-20). O papel sozinho nunca basta: sem MFA satisfeito, a operação é negada.
    /// Exemplo do template: EXCLUIR dados. Acrescente aqui as operações críticas do seu domínio.
    /// </summary>
    public static readonly FrozenSet<string> ExigemMfa =
        new[] { DadosExcluir }.ToFrozenSet(StringComparer.Ordinal);

    public static bool RequerMfa(string permissao) => ExigemMfa.Contains(permissao);

    /// <summary>
    /// Permissões de PLATAFORMA (globais, cross-tenant) — governadas por <see cref="IAutorizacaoPlataforma"/>
    /// (allowlist de administradores de plataforma), NUNCA pelo vínculo de tenant. É a face canônica da
    /// separação plataforma × tenant já documentada nas próprias chaves acima; usada pela projeção visual
    /// para saber quais permissões vêm da autorização de plataforma e não das permissões efetivas do tenant.
    /// </summary>
    public static readonly FrozenSet<string> Plataforma =
        new[]
        {
            SegurancaIncidentesGerir,
            BackupExecutar,
            RestauracaoExecutar,
            MarcaPlataformaAdministrar,
            PlataformaTenantProvisionar,
            PlataformaAdministradoresGerir,
            PlataformaEmailAdministrar,
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Verdadeiro se a chave é uma permissão de plataforma (global), não escopada a tenant.</summary>
    public static bool EhPlataforma(string permissao) => Plataforma.Contains(permissao);
}
