namespace PortalBase.Domain.Excecoes;

/// <summary>
/// Base para exceções de regra/validação do domínio. As mensagens NUNCA contêm
/// segredos nem detalhes internos de infraestrutura (regra 16).
/// </summary>
public abstract class DominioException : Exception
{
    protected DominioException()
    {
    }

    protected DominioException(string message)
        : base(message)
    {
    }

    protected DominioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Transição de estado inválida em um agregado (invariante violada).</summary>
public sealed class EstadoInvalidoException : DominioException
{
    public EstadoInvalidoException()
        : base("Transição de estado inválida.")
    {
    }

    public EstadoInvalidoException(string message)
        : base(message)
    {
    }

    public EstadoInvalidoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Entrada inválida detectada na fronteira do domínio (regras 2 e 10).</summary>
public sealed class EntradaInvalidaException : DominioException
{
    public EntradaInvalidaException()
        : base("Entrada inválida.")
    {
    }

    public EntradaInvalidaException(string message)
        : base(message)
    {
    }

    public EntradaInvalidaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
