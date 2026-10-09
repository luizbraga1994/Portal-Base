using Microsoft.Extensions.Configuration;
using PermissoesApp = PortalBase.Application.Autorizacao.Permissoes;

namespace PortalBase.Web.Ui.Autorizacao;

/// <summary>
/// Autorização VISUAL: decide se um item de menu/botão APARECE. É conveniência de UX; a decisão
/// real de autorização é sempre no servidor (<c>IServicoAutorizacao</c>/<c>IAutorizacaoPlataforma</c>)
/// a partir do catálogo por tenant (regras 3, 15, 42). Esconder um botão nunca é controle de
/// segurança — apenas evita oferecer ao usuário uma ação que ele não pode concluir.
///
/// Prompt 71 (F70-CRIT-01): FORA do modo demonstração, o conjunto exibido é uma PROJEÇÃO REAL das
/// permissões efetivas do usuário autenticado (via <see cref="IProvedorPermissoesVisuais"/>), hidratada
/// no início do circuito. Não é uma segunda fonte de verdade: as chaves vêm do catálogo central e o
/// conjunto vem das MESMAS consultas do servidor. Fail-closed até hidratar / se a fonte falhar.
/// </summary>
public interface IAutorizacaoVisual
{
    /// <summary>Se está em modo de demonstração (dados/permissões simulados e rotulados).</summary>
    bool Demonstracao { get; }

    /// <summary>Perfil de demonstração atualmente selecionado.</summary>
    string Perfil { get; }

    /// <summary>Conjunto de permissões efetivas para exibição do menu.</summary>
    IReadOnlySet<string> Permissoes { get; }

    /// <summary>Verdadeiro se o item deve aparecer. <paramref name="permissao"/> nulo = sempre visível.</summary>
    bool Pode(string? permissao);

    /// <summary>Verdadeiro se o usuário possui AO MENOS UMA das permissões (para gates compostos).</summary>
    bool PossuiAlguma(params string[] permissoes);

    /// <summary>Verdadeiro se o usuário possui TODAS as permissões informadas.</summary>
    bool PossuiTodas(params string[] permissoes);

    /// <summary>Se a permissão exige step-up MFA (espelha a política do servidor).</summary>
    bool ExigeMfa(string permissao);

    /// <summary>Troca o perfil de demonstração (só afeta a exibição; não concede acesso real).</summary>
    void DefinirPerfil(string perfil);

    /// <summary>
    /// HIDRATA o conjunto de permissões visíveis. Em produção, resolve a projeção REAL do servidor
    /// (fail-closed). Em demonstração, usa o perfil selecionado. Chamado no início do circuito
    /// (MainLayout), antes de renderizar as páginas. Idempotente por circuito.
    /// </summary>
    Task HidratarAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AutorizacaoVisual : IAutorizacaoVisual
{
    private static readonly IReadOnlySet<string> FrozenEmpty =
        new HashSet<string>(StringComparer.Ordinal);

    private readonly IProvedorPermissoesVisuais _provedor;
    private IReadOnlySet<string> _permissoes;

    public AutorizacaoVisual(IConfiguration configuracao, IProvedorPermissoesVisuais provedor)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        ArgumentNullException.ThrowIfNull(provedor);
        _provedor = provedor;
        Demonstracao = configuracao.GetValue("Ui:DadosDemonstracao", false);
        Perfil = configuracao.GetValue("Ui:PerfilDemonstracao", "Completo") ?? "Completo";
        // Fail-closed até HidratarAsync: fora de demo, vazio; em demo, o conjunto do perfil.
        _permissoes = Demonstracao ? PerfisDemonstracao.Resolver(Perfil) : FrozenEmpty;
    }

    public bool Demonstracao { get; }

    public string Perfil { get; private set; }

    public IReadOnlySet<string> Permissoes => _permissoes;

    public bool Pode(string? permissao) =>
        string.IsNullOrEmpty(permissao) || _permissoes.Contains(permissao);

    public bool PossuiAlguma(params string[] permissoes) =>
        permissoes is { Length: > 0 } && permissoes.Any(_permissoes.Contains);

    public bool PossuiTodas(params string[] permissoes) =>
        permissoes is { Length: > 0 } && permissoes.All(_permissoes.Contains);

    public bool ExigeMfa(string permissao) => PermissoesApp.RequerMfa(permissao);

    public void DefinirPerfil(string perfil)
    {
        if (!Demonstracao)
        {
            return; // fora de demonstração, o perfil vem do servidor; não há troca visual.
        }

        Perfil = string.IsNullOrWhiteSpace(perfil) ? "Completo" : perfil;
        _permissoes = PerfisDemonstracao.Resolver(Perfil);
    }

    public async Task HidratarAsync(CancellationToken cancellationToken = default)
    {
        if (Demonstracao)
        {
            _permissoes = PerfisDemonstracao.Resolver(Perfil); // demo: conjunto do perfil, sem servidor.
            return;
        }

        // Produção: projeção REAL do servidor (o provedor é fail-closed — devolve vazio em qualquer falha).
        _permissoes = await _provedor.ResolverAsync(cancellationToken).ConfigureAwait(false);
    }
}
