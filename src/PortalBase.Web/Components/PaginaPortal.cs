using Microsoft.AspNetCore.Components;
using PortalBase.Web.Seguranca;

namespace PortalBase.Web.Components;

/// <summary>
/// P62-R1 — base de TODAS as páginas (aplicada por <c>Components/Pages/_Imports.razor</c>). Antes de o ciclo de vida
/// da página começar (<c>OnInitialized*</c>/<c>OnParametersSet*</c>), aguarda — sem bloquear thread — a hidratação
/// do escopo do circuito interativo (<see cref="HidratacaoCircuito.Pronto"/>): principal, contribuinte e permissões.
/// Sem isto, no Blazor Web a página interativa podia inicializar antes da hidratação (componentes raiz chegam numa
/// mensagem separada do circuito) e cair em "Sem permissão", ou disputar o DbContext do escopo. No render
/// estático (SSR) não há circuito: a tarefa já está concluída e o comportamento é idêntico ao anterior.
/// </summary>
public abstract class PaginaPortal : ComponentBase
{
    [Inject]
    private HidratacaoCircuito Hidratacao { get; set; } = default!;

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        // O ParameterView só é válido na parte SÍNCRONA desta chamada: aplica os parâmetros antes do await e,
        // depois, executa o ciclo de vida padrão sem reaplicá-los.
        parameters.SetParameterProperties(this);

        var pronto = Hidratacao.Pronto;
        if (!pronto.IsCompleted)
        {
            await pronto.ConfigureAwait(true); // retoma no Dispatcher do circuito (componentes não são thread-safe)
        }

        await base.SetParametersAsync(ParameterView.Empty).ConfigureAwait(true);
    }
}
