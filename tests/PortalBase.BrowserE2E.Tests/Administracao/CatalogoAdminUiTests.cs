using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;
using PortalBase.Web.Ui.Dados;

namespace PortalBase.BrowserE2E.Tests.Administracao;

/// <summary>
/// Guardas das projeções de UI da administração (Prompt 69): o catálogo de permissões por domínio
/// (<see cref="CatalogoPermissoesUi"/>) e o de papéis (<see cref="CatalogoPapeisUi"/>). Garantem que a
/// administração exibe o modelo REAL e COMPLETO: toda permissão do catálogo central aparece em EXATAMENTE um
/// domínio (nada some em silêncio), as chaves são reais, a marcação de MFA espelha a política do servidor, e os
/// papéis mapeiam para as permissões canônicas. Determinístico, sem I/O.
/// </summary>
public sealed class CatalogoAdminUiTests
{
    private static string[] CatalogoDeChaves() =>
        typeof(Permissoes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    [Fact]
    public void Toda_Permissao_Do_Catalogo_Aparece_Em_Exatamente_Um_Dominio()
    {
        var todasNaUi = CatalogoPermissoesUi.Grupos.SelectMany(g => g.Permissoes).Select(p => p.Chave).ToList();

        // Nenhuma permissão do catálogo central fica de fora da administração (sem lacuna silenciosa).
        foreach (var chave in CatalogoDeChaves())
        {
            todasNaUi.Count(c => c == chave).Should().Be(1, because: $"'{chave}' deve aparecer em exatamente um domínio");
        }

        // E a UI não inventa permissões inexistentes.
        todasNaUi.Should().OnlyHaveUniqueItems();
        todasNaUi.Should().BeEquivalentTo(CatalogoDeChaves());
    }

    [Fact]
    public void Marcacao_De_MFA_Espelha_A_Politica_Do_Servidor()
    {
        foreach (var permissao in CatalogoPermissoesUi.Grupos.SelectMany(g => g.Permissoes))
        {
            permissao.ExigeMfa.Should().Be(Permissoes.RequerMfa(permissao.Chave));
        }
    }

    [Fact]
    public void TodasAsChaves_Batem_Com_A_Uniao_Dos_Grupos()
    {
        CatalogoPermissoesUi.TodasAsChaves.Should().BeEquivalentTo(
            CatalogoPermissoesUi.Grupos.SelectMany(g => g.Permissoes).Select(p => p.Chave));
    }

    [Fact]
    public void Papeis_Da_Ui_Mapeiam_Para_A_Matriz_Canonica()
    {
        CatalogoPapeisUi.Papeis.Should().HaveCount(Enum.GetValues<PapelConhecido>().Length);

        foreach (var papelUi in CatalogoPapeisUi.Papeis)
        {
            var papel = Enum.Parse<PapelConhecido>(papelUi.Papel);
            papelUi.Permissoes.Should().BeEquivalentTo(CatalogoPapeisPermissoes.ChavesDe(papel));
        }
    }
}
