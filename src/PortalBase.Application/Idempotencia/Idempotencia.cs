using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Idempotencia;

/// <summary>
/// Chave de idempotência validada (regra 12): identifica de forma estável uma
/// operação que pode ser reentregue/repetida, para que o efeito ocorra no máximo
/// uma vez (ex.: envio a uma API externa, lançamento).
/// </summary>
public sealed record IdempotencyKey
{
    public const int TamanhoMaximo = 128;

    private IdempotencyKey(string valor) => Valor = valor;

    public string Valor { get; }

    /// <exception cref="EntradaInvalidaException">Entrada nula/vazia ou longa demais.</exception>
    public static IdempotencyKey Criar(string? entrada)
    {
        if (string.IsNullOrWhiteSpace(entrada))
        {
            throw new EntradaInvalidaException("Chave de idempotência é obrigatória.");
        }

        var valor = entrada.Trim();

        if (valor.Length > TamanhoMaximo)
        {
            throw new EntradaInvalidaException("Chave de idempotência excede o tamanho máximo permitido.");
        }

        return new IdempotencyKey(valor);
    }

    public override string ToString() => Valor;
}

