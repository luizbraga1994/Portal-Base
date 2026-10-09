using System.Globalization;
using System.Text;

namespace PortalBase.Web.Ui.Navegacao;

/// <summary>
/// Um item de menu. <see cref="PermissaoNecessaria"/> é a chave RBAC exigida para o item
/// aparecer; <c>null</c> = visível para qualquer sessão autenticada. <see cref="PermissoesAlternativas"/>: o item
/// também aparece para quem tem QUALQUER uma delas (tela que reúne várias áreas, como o centro de Administração).
/// A visibilidade no menu é apenas UX — a autorização real de cada ação continua no servidor (regras 3, 15, 42).
/// </summary>
public sealed record ItemNavegacao(
    string Rotulo,
    string Rota,
    string Icone,
    string? PermissaoNecessaria = null,
    bool EmPreparacao = false,
    IReadOnlyList<string>? PermissoesAlternativas = null)
{
    /// <summary>Visível para estas permissões (a principal ou qualquer alternativa)?</summary>
    public bool VisivelPara(Autorizacao.IAutorizacaoVisual autorizacao)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        return autorizacao.Pode(PermissaoNecessaria) || (PermissoesAlternativas?.Any(autorizacao.Pode) ?? false);
    }
}

/// <summary>
/// Um grupo (módulo) de itens de menu. <see cref="Expansivel"/>: o grupo é um dropdown no menu lateral (abre/fecha;
/// o grupo da tela atual vem aberto); <c>false</c> = itens sempre à vista (ex.: "Principal"). <see cref="Icone"/>: nome
/// em <see cref="IconesLinha"/> (o mesmo vale para <see cref="ItemNavegacao.Icone"/>).
/// </summary>
public sealed record GrupoNavegacao(
    string Titulo,
    IReadOnlyList<ItemNavegacao> Itens,
    bool Expansivel = true,
    string Icone = IconesLinha.Neutro)
{
    /// <summary>Identificador estável do grupo (minúsculo, sem acento: "administracao"), usado no HTML e na preferência do navegador.</summary>
    public string Chave => CriarChave(Titulo);

    private static string CriarChave(string titulo)
    {
        var chave = new StringBuilder(titulo.Length);
        foreach (var c in titulo.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            chave.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        return chave.ToString();
    }
}

/// <summary>Um item de trilha de navegação (breadcrumb).</summary>
public sealed record Migalha(string Rotulo, string? Href = null);
