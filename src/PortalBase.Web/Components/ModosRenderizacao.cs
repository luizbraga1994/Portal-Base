using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace PortalBase.Web.Components;

/// <summary>
/// Modos de renderização das páginas do portal. UI-001 (auditoria de 08/10/2026): com o prerender ligado, toda página
/// interativa carregava os dados DUAS vezes a cada navegação — uma no prerender (SSR) e outra quando o circuito assumia
/// —, o dobro de consultas ao banco. As páginas do portal são para usuários autenticados (sem SEO): sem prerender, a
/// consulta roda uma vez, já no circuito. O layout continua estático (barra superior e menu aparecem na hora).
/// </summary>
public static class ModosRenderizacao
{
    /// <summary>Interativo no servidor, SEM prerender — use em toda página <c>@rendermode</c> do portal.</summary>
    public static IComponentRenderMode InterativoSemPrerender { get; } = new InteractiveServerRenderMode(prerender: false);
}
