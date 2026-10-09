using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Autorizacao;

/// <summary>
/// FONTE ÚNICA do modelo RBAC canônico (Prompt 69) — a matriz PAPEL → PERMISSÕES e o mapa
/// <see cref="PermissaoConhecida"/> → chave do catálogo <see cref="Permissoes"/>. É a MESMA verdade que o seed
/// do catálogo persiste (o seed CONSOME esta classe, não a reliteraliza), de modo que a administração pode
/// EXIBIR o modelo real sem inventar dados nem depender de dados de demonstração. O Administrador tem ACESSO
/// TOTAL no tenant (todas as permissões do tenant) — decisão do produto;
/// as operações de alto impacto continuam exigindo MFA (<see cref="Permissoes.ExigemMfa"/>) e as permissões de
/// PLATAFORMA (globais) continuam fora dos papéis de tenant — ver docs/IDENTIDADE-AUTORIZACAO.md. Determinístico,
/// sem I/O.
/// </summary>
public static class CatalogoPapeisPermissoes
{
    /// <summary>Matriz canônica papel → permissões (enum). O seed do catálogo deriva os pares HasData daqui.</summary>
    public static IReadOnlyDictionary<PapelConhecido, IReadOnlyList<PermissaoConhecida>> Matriz { get; } =
        new Dictionary<PapelConhecido, IReadOnlyList<PermissaoConhecida>>
        {
            // Leitor NÃO acessa a trilha de auditoria (auditoria.visualizar é administrativa).
            [PapelConhecido.Leitor] =
            [
                PermissaoConhecida.VisualizarDados,
            ],
            // O Analista opera os dados do tenant (cadastrar, editar, importar); excluir e administrar ficam fora do papel.
            [PapelConhecido.Analista] =
            [
                PermissaoConhecida.VisualizarDados,
                PermissaoConhecida.Cadastrar,
                PermissaoConhecida.Editar,
                PermissaoConhecida.Importar,
            ],
            // ACESSO TOTAL no tenant: TODAS as permissões de tenant, derivadas do enum — uma permissão nova entra
            // automaticamente. Alto impacto continua exigindo MFA (Permissoes.ExigemMfa).
            [PapelConhecido.Administrador] = [.. Enum.GetValues<PermissaoConhecida>()],
        };

    /// <summary>Mapa <see cref="PermissaoConhecida"/> → chave do catálogo <see cref="Permissoes"/> (fonte única).</summary>
    public static string ChaveDe(PermissaoConhecida permissao) => permissao switch
    {
        PermissaoConhecida.VisualizarDados => Permissoes.DadosVisualizar,
        PermissaoConhecida.Cadastrar => Permissoes.DadosCadastrar,
        PermissaoConhecida.Editar => Permissoes.DadosEditar,
        PermissaoConhecida.Excluir => Permissoes.DadosExcluir,
        PermissaoConhecida.Importar => Permissoes.DadosImportar,
        PermissaoConhecida.AdministrarTenant => Permissoes.AdminTenant,
        PermissaoConhecida.AdministrarUsuarios => Permissoes.AdminUsuarios,
        PermissaoConhecida.VisualizarAuditoria => Permissoes.AuditoriaVisualizar,
        _ => throw new ArgumentOutOfRangeException(nameof(permissao), permissao, "Permissão desconhecida."),
    };

    /// <summary>Chaves de permissão (catálogo) concedidas por um papel — pronto para exibição na administração.</summary>
    public static IReadOnlyList<string> ChavesDe(PapelConhecido papel) =>
        Matriz.TryGetValue(papel, out var permissoes)
            ? permissoes.Select(ChaveDe).ToList()
            : [];
}
