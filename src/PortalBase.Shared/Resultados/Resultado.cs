namespace PortalBase.Shared.Resultados;

/// <summary>
/// Resultado padronizado de uma operação (sucesso/falha explícita, sem exceções
/// para fluxo de negócio esperado — regra 1). Um resultado é sucesso XOR possui erro.
/// </summary>
public class Resultado
{
    private protected Resultado(bool sucesso, Erro? erro)
    {
        if (sucesso && erro is not null)
        {
            throw new InvalidOperationException("Resultado de sucesso não pode conter erro.");
        }

        if (!sucesso && erro is null)
        {
            throw new InvalidOperationException("Resultado de falha deve conter erro.");
        }

        Sucesso = sucesso;
        Erro = erro;
    }

    public bool Sucesso { get; }

    public bool Falhou => !Sucesso;

    public Erro? Erro { get; }

    public static Resultado Ok() => new(true, null);

    public static Resultado Falha(Erro erro) => new(false, erro);

    public static Resultado<T> Ok<T>(T valor) => new(valor, true, null);

    public static Resultado<T> Falha<T>(Erro erro) => new(default, false, erro);
}

/// <summary>Resultado que carrega um valor em caso de sucesso.</summary>
public sealed class Resultado<T> : Resultado
{
    private readonly T? _valor;

    internal Resultado(T? valor, bool sucesso, Erro? erro)
        : base(sucesso, erro)
        => _valor = valor;

    /// <summary>Valor do resultado. Lança se acessado em um resultado de falha.</summary>
    public T Valor => Sucesso
        ? _valor!
        : throw new InvalidOperationException("Resultado de falha não possui valor.");
}
