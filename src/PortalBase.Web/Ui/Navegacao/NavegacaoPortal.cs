using PortalBase.Web.Ui.Autorizacao;
using PermissoesApp = PortalBase.Application.Autorizacao.Permissoes;

namespace PortalBase.Web.Ui.Navegacao;

/// <summary>
/// Estrutura de navegação do Portal Base (módulos → itens), com a permissão RBAC de cada item.
/// <see cref="Filtrar"/> devolve apenas os grupos/itens visíveis para as permissões correntes —
/// mas isto é UX: nenhuma rota é protegida pela ausência no menu (regras 3, 15, 42).
/// </summary>
public static class NavegacaoPortal
{
    /// <summary>Árvore completa (antes da filtragem RBAC).</summary>
    public static IReadOnlyList<GrupoNavegacao> Grupos { get; } =
    [
        new GrupoNavegacao("Principal",
        [
            new ItemNavegacao("Dashboard", "/", "grade"),
        ],
        Expansivel: false,
        Icone: "grade"),

        // Telas do domínio do seu portal: acrescente grupos/itens aqui. A permissão do menu = a EXIGIDA pela consulta
        // autorizada no servidor (o menu é só UX — nenhuma rota é protegida pela ausência no menu).
        new GrupoNavegacao("Dados",
        [
            // Envio de arquivos (upload seguro: conteúdo real, antivírus, hash, área privada) + lista dos enviados.
            new ItemNavegacao("Arquivos", "/dados/arquivos", "arquivo", PermissoesApp.DadosVisualizar),
        ],
        Icone: "banco"),

        new GrupoNavegacao("Administração",
        [
            // Centro de administração (Prompt 69): composição REAL de contexto/operação/áreas.
            // Aparece para quem administra QUALQUER área do centro (a própria página mostra só as áreas permitidas).
            new ItemNavegacao("Administração", "/admin", "ferramenta", PermissoesApp.AdminUsuarios,
                PermissoesAlternativas: [PermissoesApp.AdminTenant]),
            // Contribuintes (plataforma): cadastrar novos tenants — só o Administrador da plataforma (permissão de plataforma).
            new ItemNavegacao("Contribuintes", "/admin/contribuintes", "predio", PermissoesApp.PlataformaTenantProvisionar),
            // Usuários (Prompt 72) — tela REAL: listagem por tenant (read-model autorizado) + ações reais
            // (revogar acesso, atribuir papel). A permissão do menu = a exigida pela consulta no servidor.
            new ItemNavegacao("Usuários", "/admin/usuarios", "usuarios", PermissoesApp.AdminUsuarios),
            // Perfis e Permissões (Prompt 69): telas REAIS sobre o catálogo canônico (não mais em preparação).
            new ItemNavegacao("Perfis", "/admin/perfis", "perfis", PermissoesApp.AdminUsuarios),
            new ItemNavegacao("Permissões", "/admin/permissoes", "chave", PermissoesApp.AdminUsuarios),
            // Tenant (Prompt 71) — tela REAL sobre read-model autorizado (metadados não secretos do contribuinte).
            // Nunca expõe segredo/base física.
            new ItemNavegacao("Tenant", "/admin/tenant", "coluna", PermissoesApp.AdminTenant),
        ],
        Icone: "escudo"),

        // Configurações do portal e do contribuinte. "E-mail (SMTP)" é de PLATAFORMA (um só servidor de e-mail para o
        // portal todo); os demais, do contribuinte selecionado. As rotas continuam as mesmas.
        new GrupoNavegacao("Configurações",
        [
            new ItemNavegacao("E-mail (SMTP)", "/configuracoes/email", "envelope", PermissoesApp.PlataformaEmailAdministrar),
            new ItemNavegacao("Marca / Logo", "/admin/marca", "imagem", PermissoesApp.AdminTenant),
        ],
        Icone: "engrenagem"),

        // Frontend Administração e Operações (Prompt 40) — telas REAIS ligadas aos read-models de
        // plataforma/auditoria (a permissão do menu = a exigida pela consulta autorizada no servidor).
        new GrupoNavegacao("Auditoria",
        [
            new ItemNavegacao("Eventos de auditoria", "/auditoria/eventos", "historico", PermissoesApp.AuditoriaVisualizar),
            new ItemNavegacao("Operações", "/auditoria/operacoes", "lupa", PermissoesApp.AuditoriaVisualizar),
            new ItemNavegacao("Incidentes", "/auditoria/incidentes", "sirene", PermissoesApp.SegurancaIncidentesGerir),
        ],
        Icone: "olho"),

        new GrupoNavegacao("Operações",
        [
            new ItemNavegacao("Saúde", "/operacoes/saude", "pulso", PermissoesApp.SegurancaIncidentesGerir),
            new ItemNavegacao("Workers", "/operacoes/workers", "robo", PermissoesApp.SegurancaIncidentesGerir),
            new ItemNavegacao("Integrações", "/operacoes/integracoes", "elo", PermissoesApp.SegurancaIncidentesGerir),
            new ItemNavegacao("Backups", "/operacoes/backups", "banco", PermissoesApp.BackupExecutar),
        ],
        Icone: "pulso"),
    ];

    /// <summary>
    /// Telas de PLATAFORMA que abrem SEM contribuinte selecionado: numa instalação nova não há contribuinte nenhum, e o
    /// Administrador da plataforma precisa cadastrar o primeiro (e configurar o e-mail). Nenhuma delas lê dado de
    /// contribuinte; a autorização de cada ação continua no servidor (permissão de plataforma).
    /// </summary>
    public static IReadOnlySet<string> RotasSemContribuinte { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/admin/contribuintes", "/configuracoes/email" };

    /// <summary>Menu do modo "sem contribuinte": só as telas de <see cref="RotasSemContribuinte"/> que o usuário pode abrir.</summary>
    public static IReadOnlyList<GrupoNavegacao> FiltrarSemContribuinte(IAutorizacaoVisual autorizacao)
        => Filtrar(autorizacao)
            .Select(g => g with { Itens = [.. g.Itens.Where(i => RotasSemContribuinte.Contains(i.Rota))] })
            .Where(g => g.Itens.Count > 0)
            .ToList();

    /// <summary>A rota (caminho, sem query) é uma tela que abre sem contribuinte?</summary>
    public static bool AbreSemContribuinte(string? caminho)
        => RotasSemContribuinte.Contains(NormalizarCaminho(caminho));

    /// <summary>Grupos/itens visíveis para as permissões correntes (grupos vazios são omitidos).</summary>
    public static IReadOnlyList<GrupoNavegacao> Filtrar(IAutorizacaoVisual autorizacao)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);

        var resultado = new List<GrupoNavegacao>();
        foreach (var grupo in Grupos)
        {
            var itens = grupo.Itens.Where(i => i.VisivelPara(autorizacao)).ToList();
            if (itens.Count > 0)
            {
                resultado.Add(grupo with { Itens = itens });
            }
        }

        return resultado;
    }

    /// <summary>
    /// O item do menu que corresponde ao endereço atual: o de rota mais LONGA que é prefixo do caminho (com separador
    /// "/"), para "/admin/usuarios/novo" destacar só "Usuários" (e não também "Administração", rota "/admin") e
    /// "/admin/usuarios/{id}" destacar "Usuários". "/" só corresponde ao próprio "/". Sem correspondência: <c>null</c>.
    /// </summary>
    public static ItemNavegacao? ItemAtivo(IEnumerable<GrupoNavegacao> grupos, string? caminho)
    {
        ArgumentNullException.ThrowIfNull(grupos);

        var atual = NormalizarCaminho(caminho);
        ItemNavegacao? melhor = null;
        foreach (var item in grupos.SelectMany(g => g.Itens))
        {
            if (Corresponde(item.Rota, atual) && (melhor is null || item.Rota.Length > melhor.Rota.Length))
            {
                melhor = item;
            }
        }

        return melhor;
    }

    private static string NormalizarCaminho(string? caminho)
    {
        var c = caminho ?? string.Empty;
        var corte = c.IndexOfAny(['?', '#']);
        if (corte >= 0)
        {
            c = c[..corte];
        }

        return "/" + c.Trim('/');
    }

    private static bool Corresponde(string rota, string caminho)
        => rota == "/"
            ? caminho == "/"
            : caminho.Equals(rota, StringComparison.OrdinalIgnoreCase)
              || caminho.StartsWith(rota + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Localiza um item por rota (normaliza barra final e prefixo).</summary>
    public static ItemNavegacao? EncontrarPorRota(string? rota)
    {
        var alvo = "/" + (rota ?? string.Empty).Trim('/');
        return Grupos.SelectMany(g => g.Itens).FirstOrDefault(i => i.Rota == alvo);
    }
}
