using System.Collections.Frozen;

namespace PortalBase.Application.Autorizacao;

/// <summary>
/// Conjunto EFETIVO de permissões de um usuário em um tenant específico, derivado no
/// servidor a partir do vínculo ativo usuário×tenant e dos papéis escopados a esse vínculo
/// (seção 11). Deny-by-default: uma chave ausente significa NÃO autorizado.
/// </summary>
public sealed class PermissoesTenant
{
    public static readonly PermissoesTenant Nenhuma = new([]);

    private readonly FrozenSet<string> _chaves;

    public PermissoesTenant(IEnumerable<string> chaves)
    {
        ArgumentNullException.ThrowIfNull(chaves);
        _chaves = chaves.ToFrozenSet(StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Chaves => _chaves;

    public bool Possui(string permissao) => _chaves.Contains(permissao);
}
