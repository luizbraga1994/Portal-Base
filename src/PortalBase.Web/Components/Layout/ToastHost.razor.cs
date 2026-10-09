using Microsoft.AspNetCore.Components;
using PortalBase.Web.Ui.Notificacoes;

namespace PortalBase.Web.Components.Layout;

/// <summary>Host visual das notificações (toasts). Assina o serviço por circuito e redesenha.</summary>
public sealed partial class ToastHost : ComponentBase, IDisposable
{
    [Inject]
    public ServicoNotificacoes Notificacoes { get; set; } = default!;

    protected override void OnInitialized() => Notificacoes.Alterado += AoAlterar;

    public void Dispose()
    {
        Notificacoes.Alterado -= AoAlterar;
        GC.SuppressFinalize(this);
    }

    private void AoAlterar(object? sender, EventArgs e) => _ = InvokeAsync(StateHasChanged);

    private static string IconeToast(string tipo) => tipo switch
    {
        "ok" => "✓",
        "bad" => "✕",
        "warn" => "!",
        _ => "ℹ",
    };
}
