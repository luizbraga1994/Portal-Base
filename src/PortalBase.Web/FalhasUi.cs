using System.Runtime.CompilerServices;

namespace PortalBase.Web;

/// <summary>
/// Registro das FALHAS INESPERADAS da interface — OBS-002 (auditoria de 08/10/2026): os blocos <c>catch (Exception)</c>
/// das telas e dos serviços de tela trocavam a exceção por uma mensagem genérica ao usuário (regra 16) e não deixavam
/// rastro nenhum no servidor; a falha ficava invisível. Agora cada um chama <see cref="Registrar"/> antes de responder:
/// nível Error, com o componente, a operação, o tipo da exceção e a própria exceção (stack trace só no log do servidor,
/// nunca na tela). O CorrelationId da requisição/circuito entra pelo contexto do Serilog.
/// <para>Estático de propósito, como o <c>Serilog.Log</c>: são ~100 pontos em ~40 classes de tela, e injetar um logger
/// em cada uma mudaria todos os construtores sem ganho. O teste <c>FalhasUiTests</c> impede que um <c>catch</c> genérico
/// novo fique sem registro.</para>
/// </summary>
public static class FalhasUi
{
    private static readonly AsyncLocal<Serilog.ILogger?> Substituto = new();

    private static Serilog.ILogger Destino => Substituto.Value ?? Serilog.Log.Logger;

    /// <summary>Registra a falha (Error). Nunca lança: quem chama já está tratando uma falha.</summary>
    public static void Registrar(Exception excecao, [CallerMemberName] string operacao = "", [CallerFilePath] string arquivo = "")
    {
        if (excecao is null)
        {
            return;
        }

        try
        {
            var componente = Path.GetFileNameWithoutExtension(arquivo);
            Destino.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "PortalBase.Web.Ui." + componente)
                .Error(excecao, "Falha na tela {Componente}.{Operacao} ({TipoErro}); o usuário recebeu uma mensagem genérica.",
                    componente, operacao, excecao.GetType().Name);
        }
#pragma warning disable CA1031 // O registro de uma falha nunca pode virar outra falha na tela.
        catch (Exception)
#pragma warning restore CA1031
        {
            // sem log possível: nada a fazer
        }
    }

    /// <summary>Para testes: direciona os registros do fluxo assíncrono atual para <paramref name="destino"/> até o descarte.</summary>
    public static IDisposable Capturar(Serilog.ILogger destino)
    {
        ArgumentNullException.ThrowIfNull(destino);
        var anterior = Substituto.Value;
        Substituto.Value = destino;
        return new Restauracao(anterior);
    }

    private sealed class Restauracao(Serilog.ILogger? anterior) : IDisposable
    {
        public void Dispose() => Substituto.Value = anterior;
    }
}
