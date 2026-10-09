using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Autorizacao;

/// <summary>
/// Guardas do catálogo canônico PAPEL→PERMISSÃO (Prompt 69) — a fonte única que o seed persiste E a
/// administração exibe. Travam regressões: cobertura de papéis, chaves válidas do catálogo, mapeamento total de
/// <see cref="PermissaoConhecida"/>, e o ACESSO TOTAL do Administrador no contribuinte (sem dispensar o MFA).
/// </summary>
public sealed class CatalogoPapeisPermissoesTests
{
    private static System.Collections.Generic.HashSet<string> CatalogoDeChaves() =>
        typeof(Permissoes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Matriz_Cobre_Todos_Os_Papeis_Com_Permissoes()
    {
        foreach (var papel in Enum.GetValues<PapelConhecido>())
        {
            CatalogoPapeisPermissoes.Matriz.Should().ContainKey(papel);
            CatalogoPapeisPermissoes.Matriz[papel].Should().NotBeEmpty(because: $"o papel {papel} precisa conceder ao menos uma permissão");
        }
    }

    [Fact]
    public void ChaveDe_Mapeia_Todas_As_Permissoes_Conhecidas_Para_Chaves_Reais()
    {
        var catalogo = CatalogoDeChaves();
        foreach (var permissao in Enum.GetValues<PermissaoConhecida>())
        {
            var chave = CatalogoPapeisPermissoes.ChaveDe(permissao); // não lança ArgumentOutOfRange
            catalogo.Should().Contain(chave, because: $"a permissão {permissao} deve mapear para uma chave real do catálogo");
        }
    }

    [Fact]
    public void ChavesDe_De_Cada_Papel_Sao_Chaves_Reais()
    {
        var catalogo = CatalogoDeChaves();
        foreach (var papel in Enum.GetValues<PapelConhecido>())
        {
            foreach (var chave in CatalogoPapeisPermissoes.ChavesDe(papel))
            {
                catalogo.Should().Contain(chave);
            }
        }
    }

    [Fact]
    public void Administrador_Tem_Acesso_Total_No_Contribuinte()
    {
        // Decisão do produto: o Administrador tem TODAS as permissões de tenant.
        var admin = CatalogoPapeisPermissoes.ChavesDe(PapelConhecido.Administrador);
        var todas = Enum.GetValues<PermissaoConhecida>().Select(CatalogoPapeisPermissoes.ChaveDe).ToList();

        admin.Should().BeEquivalentTo(todas);
        admin.Should().OnlyHaveUniqueItems();
        admin.Should().Contain([Permissoes.DadosExcluir, Permissoes.AdminTenant, Permissoes.AdminUsuarios, Permissoes.AuditoriaVisualizar]);
    }

    [Fact]
    public void Acesso_Total_Nao_Dispensa_O_Mfa_Nem_Inclui_Permissoes_De_Plataforma()
    {
        var admin = CatalogoPapeisPermissoes.ChavesDe(PapelConhecido.Administrador);

        // O papel sozinho nunca basta para alto impacto: sem MFA satisfeito o servidor nega (Permissoes.ExigemMfa).
        Permissoes.ExigemMfa.Should().Contain(Permissoes.DadosExcluir);
        // Permissões GLOBAIS (cross-tenant) vêm da allowlist de plataforma, nunca de um papel de tenant.
        admin.Should().NotContain(Permissoes.Plataforma);
    }

    [Fact]
    public void Analista_Opera_Os_Dados_Sem_Excluir_Nem_Administrar()
    {
        var analista = CatalogoPapeisPermissoes.ChavesDe(PapelConhecido.Analista);

        analista.Should().Contain([Permissoes.DadosVisualizar, Permissoes.DadosCadastrar, Permissoes.DadosEditar, Permissoes.DadosImportar]);
        analista.Should().NotContain([Permissoes.DadosExcluir, Permissoes.AdminUsuarios, Permissoes.AdminTenant, Permissoes.AuditoriaVisualizar]);
        analista.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Leitor_So_Consulta()
    {
        var leitor = CatalogoPapeisPermissoes.ChavesDe(PapelConhecido.Leitor);
        leitor.Should().ContainSingle().Which.Should().Be(Permissoes.DadosVisualizar);
        leitor.Should().NotContain(Permissoes.AuditoriaVisualizar);
    }
}
