using System.Collections.Frozen;
using PortalBase.Application.Autorizacao;

namespace PortalBase.Web.Ui.Autorizacao;

/// <summary>
/// Conjuntos de permissão APENAS PARA DEMONSTRAÇÃO da UI (quando <c>Ui:DadosDemonstracao=true</c>).
/// Espelham a matriz papel→permissão do seed do catálogo, para exibir a filtragem de menu por RBAC
/// sem um banco. NUNCA são fonte de autorização real: toda ação continua autorizada no servidor por
/// <see cref="IServicoAutorizacao"/> a partir do catálogo por tenant (regras 3, 15, 42).
/// </summary>
public static class PerfisDemonstracao
{
    private static readonly FrozenSet<string> Leitor =
        new[] { Permissoes.DadosVisualizar }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Analista =
        new[]
        {
            Permissoes.DadosVisualizar, Permissoes.DadosCadastrar, Permissoes.DadosEditar, Permissoes.DadosImportar,
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Administrador =
        new[]
        {
            Permissoes.DadosVisualizar, Permissoes.DadosCadastrar, Permissoes.DadosEditar, Permissoes.DadosExcluir,
            Permissoes.DadosImportar, Permissoes.AuditoriaVisualizar, Permissoes.AdminTenant, Permissoes.AdminUsuarios,
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Completo =
        Administrador.Concat(Permissoes.Plataforma).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Perfis oferecidos no seletor de demonstração.</summary>
    public static IReadOnlyList<string> Nomes { get; } =
        ["Completo", "Administrador", "Analista", "Leitor"];

    /// <summary>Resolve o conjunto de permissões de um perfil de demonstração (padrão: Completo).</summary>
    public static IReadOnlySet<string> Resolver(string? perfil) => perfil switch
    {
        "Leitor" => Leitor,
        "Analista" => Analista,
        "Administrador" => Administrador,
        _ => Completo,
    };
}
