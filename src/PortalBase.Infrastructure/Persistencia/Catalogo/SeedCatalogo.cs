using System.Globalization;
using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Infrastructure.Persistencia.Catalogo;

/// <summary>
/// Dados de seed DETERMINÍSTICOS e IDEMPOTENTES do catálogo (papéis, permissões e o
/// mapeamento papel→permissão). GUIDs são fixos (nunca aleatórios por execução), o que
/// permite ao <c>HasData</c> gerar migrations idempotentes que não duplicam registros.
///
/// DECISÃO PENDENTE: a matriz completa papel→permissão depende da documentação v2.0;
/// abaixo há um mapeamento inicial CONSERVADOR, coerente com o nome de cada papel.
/// </summary>
internal static class SeedCatalogo
{
    public static Guid PapelId(PapelConhecido papel) => ComSufixo("11111111-1111-1111-1111", (int)papel);

    public static Guid PermissaoId(PermissaoConhecida permissao) => ComSufixo("22222222-2222-2222-2222", (int)permissao);

    // FONTE ÚNICA (Prompt 68 H1 + Prompt 69): a chave de cada permissão e a matriz papel→permissão vêm do
    // catálogo canônico <see cref="CatalogoPapeisPermissoes"/> (Application). O seed NÃO reliteraliza nem
    // redefine a matriz — deriva dela. Assim a administração exibe o MESMO modelo real que é persistido, e é
    // impossível divergir por typo. Paridade reforçada por teste.
    public static string ChavePermissao(PermissaoConhecida permissao) => CatalogoPapeisPermissoes.ChaveDe(permissao);

    /// <summary>Matriz papel→permissão DERIVADA do catálogo canônico (fonte única — Prompt 69).</summary>
    public static IReadOnlyList<(PapelConhecido Papel, PermissaoConhecida Permissao)> MapaPapelPermissao { get; } =
        Construir();

    private static Guid ComSufixo(string prefixo, int valor)
    {
        var sufixo = valor.ToString("D12", CultureInfo.InvariantCulture);
        return new Guid($"{prefixo}-{sufixo}");
    }

    // Deriva os pares (papel, permissão) da matriz canônica única (CatalogoPapeisPermissoes). Ordem estável
    // (papel, depois a ordem declarada das permissões) para HasData determinístico.
    private static (PapelConhecido, PermissaoConhecida)[] Construir() =>
        [.. CatalogoPapeisPermissoes.Matriz.SelectMany(par => par.Value.Select(permissao => (par.Key, permissao)))];
}
