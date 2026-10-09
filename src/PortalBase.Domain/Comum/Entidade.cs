namespace PortalBase.Domain.Comum;

/// <summary>
/// Base para entidades com identidade. Igualdade por tipo + <see cref="Id"/>
/// (duas entidades do mesmo tipo com o mesmo Id são a mesma entidade).
/// </summary>
public abstract class Entidade<TId>
    where TId : notnull
{
    protected Entidade(TId id) => Id = id;

    public TId Id { get; }

    public override bool Equals(object? obj)
        => obj is Entidade<TId> outra
           && outra.GetType() == GetType()
           && EqualityComparer<TId>.Default.Equals(Id, outra.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
