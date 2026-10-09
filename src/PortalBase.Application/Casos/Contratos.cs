using PortalBase.Shared.Resultados;

namespace PortalBase.Application.Casos;

/// <summary>Comando que altera estado e retorna <typeparamref name="TResposta"/>.</summary>
public interface IComando<TResposta>;

/// <summary>Manipulador de um comando com resposta.</summary>
public interface IManipuladorComando<in TComando, TResposta>
    where TComando : IComando<TResposta>
{
    Task<Resultado<TResposta>> ManipularAsync(TComando comando, CancellationToken cancellationToken = default);
}
