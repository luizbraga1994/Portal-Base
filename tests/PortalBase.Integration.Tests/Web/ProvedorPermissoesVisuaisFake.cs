using PortalBase.Web.Ui.Autorizacao;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Fake do provedor de permissões visuais (Prompt 71) para testes: devolve um conjunto fixo em
/// <see cref="HidratarAsync"/>. Em modo demonstração o <see cref="AutorizacaoVisual"/> não consulta o
/// provedor (usa o perfil), então um provedor vazio serve aos testes de demonstração.
/// </summary>
public sealed class ProvedorPermissoesVisuaisFake : IProvedorPermissoesVisuais
{
    private readonly IReadOnlySet<string> _conjunto;

    public ProvedorPermissoesVisuaisFake(params string[] permissoes) =>
        _conjunto = new HashSet<string>(permissoes, StringComparer.Ordinal);

    public static ProvedorPermissoesVisuaisFake Vazio => new();

    public int Chamadas { get; private set; }

    public Task<IReadOnlySet<string>> ResolverAsync(CancellationToken cancellationToken = default)
    {
        Chamadas++;
        return Task.FromResult(_conjunto);
    }
}
