using PermissoesApp = PortalBase.Application.Autorizacao.Permissoes;

namespace PortalBase.Web.Ui.Navegacao;

/// <summary>
/// Uma área do centro de administração (<c>/admin</c>) com o estado REAL da sua ligação ao backend.
/// <see cref="Permissao"/> é a mesma do item de menu da <see cref="Rota"/> (menu = página = servidor).
/// </summary>
public sealed record AreaAdministracao(
    string Titulo,
    string Rota,
    string Permissao,
    string EstadoRotulo,
    string EstadoBadge,
    string Observacao);

/// <summary>
/// Áreas exibidas no centro de administração (Prompt 69), com estado HONESTO: cada observação diz o que a tela faz hoje
/// (ex.: cadastro de usuários, cadastro de contribuintes pela plataforma).
/// </summary>
public static class AreasAdministracao
{
    private const string Ligada = "Ligada";
    private const string BadgeOk = "ok";

    public static IReadOnlyList<AreaAdministracao> Todas { get; } =
    [
        new("Permissões", "/admin/permissoes", PermissoesApp.AdminUsuarios, Ligada, BadgeOk, "Catálogo real de permissões por domínio."),
        new("Perfis (papéis)", "/admin/perfis", PermissoesApp.AdminUsuarios, Ligada, BadgeOk, "Matriz canônica papel→permissão."),
        new("Marca / Logo", "/admin/marca", PermissoesApp.AdminTenant, Ligada, BadgeOk, "Administração da marca."),
        new("Auditoria", "/auditoria/eventos", PermissoesApp.AuditoriaVisualizar, Ligada, BadgeOk, "Consulta paginada server-side."),
        new("Workers / Operação", "/operacoes/workers", PermissoesApp.SegurancaIncidentesGerir, Ligada, BadgeOk, "Saúde real (P67)."),
        new("Contribuintes (plataforma)", "/admin/contribuintes", PermissoesApp.PlataformaTenantProvisionar, Ligada, BadgeOk,
            "Cadastro de novos contribuintes com a base de dados criada e preparada pelo portal. Só o Administrador da plataforma."),
        new("Usuários", "/admin/usuarios", PermissoesApp.AdminUsuarios, Ligada, BadgeOk,
            "Cadastro, edição, perfis, acesso e exclusão dos usuários do contribuinte."),
        new("Tenant", "/admin/tenant", PermissoesApp.AdminTenant, Ligada, BadgeOk,
            "Dados do contribuinte e ciclo de vida: ativar, desabilitar, reativar."),
    ];

    /// <summary>Quantidade de áreas ligadas ao backend real.</summary>
    public static int Ligadas => Todas.Count(a => a.EstadoBadge == BadgeOk);

    /// <summary>Quantidade de áreas com lacuna registrada (parcial/pendente).</summary>
    public static int ComLacuna => Todas.Count(a => a.EstadoBadge != BadgeOk);
}
