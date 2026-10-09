namespace PortalBase.Web.Ui.Notificacoes;

/// <summary>Uma notificação (toast) de UI. Nunca carrega payload/segredo (regras 8, 45).</summary>
public sealed record Notificacao(Guid Id, string Tipo, string Titulo, string Mensagem);

/// <summary>
/// Serviço de notificações (toasts) por circuito. Feedback de UX consistente para sucesso/erro/
/// aviso/info — substitui <c>alert()</c> do navegador (§46).
/// </summary>
public sealed class ServicoNotificacoes
{
    private readonly List<Notificacao> _ativas = [];

    /// <summary>Disparado quando a lista muda (para o host redesenhar).</summary>
    public event EventHandler? Alterado;

    public IReadOnlyList<Notificacao> Ativas => _ativas;

    public void Sucesso(string titulo, string mensagem = "") => Adicionar("ok", titulo, mensagem);

    public void Erro(string titulo, string mensagem = "") => Adicionar("bad", titulo, mensagem);

    public void Aviso(string titulo, string mensagem = "") => Adicionar("warn", titulo, mensagem);

    public void Info(string titulo, string mensagem = "") => Adicionar("info", titulo, mensagem);

    public void Remover(Guid id)
    {
        _ativas.RemoveAll(n => n.Id == id);
        Alterado?.Invoke(this, EventArgs.Empty);
    }

    private void Adicionar(string tipo, string titulo, string mensagem)
    {
        _ativas.Add(new Notificacao(Guid.NewGuid(), tipo, titulo, mensagem));
        Alterado?.Invoke(this, EventArgs.Empty);
    }
}
