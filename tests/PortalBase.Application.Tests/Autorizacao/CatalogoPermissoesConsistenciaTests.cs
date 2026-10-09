using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using PortalBase.Application.Autorizacao;

namespace PortalBase.Application.Tests.Autorizacao;

/// <summary>
/// Guarda de CONSISTÊNCIA do catálogo central de permissões (Prompt 68, H1). O catálogo
/// <see cref="Permissoes"/> é a FONTE ÚNICA das chaves de autorização (o seed agora referencia estas mesmas
/// constantes — não reliteraliza). Estes testes travam regressões silenciosas: chaves duplicadas, formato
/// inválido, ou uma entrada de <see cref="Permissoes.ExigemMfa"/> que não seja uma permissão real do catálogo
/// (o que faria o step-up nunca casar). Determinístico, sem I/O.
/// </summary>
public sealed partial class CatalogoPermissoesConsistenciaTests
{
    private static List<string> ConstantesDeChave() =>
        typeof(Permissoes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void Chaves_Sao_Unicas()
    {
        var chaves = ConstantesDeChave();
        chaves.Should().OnlyHaveUniqueItems(because: "cada permissão precisa de uma chave distinta");
    }

    [Fact]
    public void Chaves_Seguem_Formato_Canonico()
    {
        // Formato estável (minúsculas, segmentos separados por ponto) — o mesmo persistido no catálogo/seed.
        foreach (var chave in ConstantesDeChave())
        {
            FormatoChave().IsMatch(chave).Should().BeTrue(because: $"'{chave}' deve seguir o formato canônico de permissão");
        }
    }

    [Fact]
    public void ExigemMfa_So_Contem_Permissoes_Reais_Do_Catalogo()
    {
        // Uma entrada de ExigemMfa que não exista no catálogo faria RequerMfa() nunca disparar (falha silenciosa).
        var catalogo = ConstantesDeChave().ToHashSet(StringComparer.Ordinal);
        foreach (var exigeMfa in Permissoes.ExigemMfa)
        {
            catalogo.Should().Contain(exigeMfa, because: "toda permissão de alto impacto tem de existir no catálogo");
        }
    }

    [Fact]
    public void RequerMfa_Reflete_O_Conjunto_ExigemMfa()
    {
        Permissoes.RequerMfa(Permissoes.DadosExcluir).Should().BeTrue();
        Permissoes.RequerMfa(Permissoes.DadosVisualizar).Should().BeFalse();
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z0-9]+)+$")]
    private static partial Regex FormatoChave();
}
