using PortalBase.Application.Autorizacao;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Web.Ui.Dados;

// =====================================================================================================
// PROJEÇÕES DE UI DA ADMINISTRAÇÃO (Prompt 69). Somente LEITURA e SOMENTE dados REAIS do modelo de
// autorização: o catálogo central de permissões (Application.Autorizacao.Permissoes) e a matriz canônica
// papel→permissão (CatalogoPapeisPermissoes) — a MESMA verdade persistida pelo seed. Não há dados fictícios,
// nem strings de permissão soltas (as chaves vêm SEMPRE das constantes do catálogo). Rótulos/descrições são
// apresentação (não são constantes funcionais). A autorização real é sempre do servidor.
// =====================================================================================================

/// <summary>Uma permissão do catálogo, projetada para exibição (chave real + rótulo + se exige MFA).</summary>
public sealed record PermissaoUi(string Chave, string Rotulo, bool ExigeMfa);

/// <summary>Grupo de permissões por DOMÍNIO (Dados, Administração, Segurança…).</summary>
public sealed record GrupoPermissoesUi(string Dominio, string Descricao, IReadOnlyList<PermissaoUi> Permissoes);

/// <summary>Um papel (role) do catálogo, com suas permissões canônicas (chaves reais).</summary>
public sealed record PapelUi(string Papel, string Rotulo, string Descricao, IReadOnlyList<string> Permissoes);

/// <summary>
/// Catálogo de PERMISSÕES agrupado por domínio (Prompt 69, Fases 8/28). As CHAVES são as constantes reais de
/// <see cref="Permissoes"/> (nunca literais no frontend); o rótulo é apenas apresentação. A marcação de MFA
/// espelha <see cref="Permissoes.RequerMfa"/> (política do servidor). Puro/determinístico.
/// </summary>
public static class CatalogoPermissoesUi
{
    private static PermissaoUi P(string chave, string rotulo) => new(chave, rotulo, Permissoes.RequerMfa(chave));

    public static IReadOnlyList<GrupoPermissoesUi> Grupos { get; } =
    [
        new("Dados", "Operações sobre os dados do tenant.",
        [
            P(Permissoes.DadosVisualizar, "Visualizar dados"),
            P(Permissoes.DadosCadastrar, "Cadastrar dados"),
            P(Permissoes.DadosEditar, "Editar dados"),
            P(Permissoes.DadosExcluir, "Excluir dados"),
            P(Permissoes.DadosImportar, "Importar arquivos"),
        ]),
        new("Administração", "Administração do tenant e dos usuários.",
        [
            P(Permissoes.AdminTenant, "Administrar tenant"),
            P(Permissoes.AdminUsuarios, "Administrar usuários"),
        ]),
        new("Auditoria", "Consulta da trilha de auditoria.",
        [
            P(Permissoes.AuditoriaVisualizar, "Visualizar auditoria"),
        ]),
        new("Segurança / Plataforma", "Operações GLOBAIS de plataforma (todos os contribuintes) — do Administrador da plataforma.",
        [
            P(Permissoes.SegurancaIncidentesGerir, "Gerir incidentes de segurança"),
            P(Permissoes.BackupExecutar, "Executar backup"),
            P(Permissoes.RestauracaoExecutar, "Executar restauração"),
            P(Permissoes.MarcaPlataformaAdministrar, "Administrar marca da plataforma"),
            // Prompt 71 (P2-14): provisionar/criar contribuinte é operação de PLATAFORMA (nasce sem tenant),
            // governada por IAutorizacaoPlataforma — nunca por admin.tenant. Aparece aqui, no domínio de plataforma.
            P(Permissoes.PlataformaTenantProvisionar, "Provisionar contribuinte (tenant)"),
            P(Permissoes.PlataformaAdministradoresGerir, "Gerir administradores da plataforma"),
            P(Permissoes.PlataformaEmailAdministrar, "Configurar o e-mail de disparo (SMTP)"),
        ]),
    ];

    /// <summary>Todas as chaves de permissão apresentadas (para checagens de completude/testes).</summary>
    public static IReadOnlySet<string> TodasAsChaves { get; } =
        Grupos.SelectMany(g => g.Permissoes).Select(p => p.Chave).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Catálogo de PAPÉIS (roles) canônicos (Prompt 69, Fase 7). Papéis = enum de domínio <see cref="PapelConhecido"/>;
/// as permissões de cada papel vêm da matriz canônica <see cref="CatalogoPapeisPermissoes"/> (fonte única, a mesma
/// que o seed persiste). Sem dados de demonstração. Rótulo/descrição são apresentação.
/// </summary>
public static class CatalogoPapeisUi
{
    public static IReadOnlyList<PapelUi> Papeis { get; } =
    [
        Mapear(PapelConhecido.Leitor, "Leitor", "Somente consulta de dados. Não acessa a trilha de auditoria."),
        Mapear(PapelConhecido.Analista, "Analista", "Cadastro, edição e importação de dados."),
        Mapear(PapelConhecido.Administrador, "Administrador", "Acesso total no tenant: todas as permissões. As operações de alto impacto continuam exigindo MFA."),
    ];

    private static PapelUi Mapear(PapelConhecido papel, string rotulo, string descricao) =>
        new(papel.ToString(), rotulo, descricao, CatalogoPapeisPermissoes.ChavesDe(papel));
}
