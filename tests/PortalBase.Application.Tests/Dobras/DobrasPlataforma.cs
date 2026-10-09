using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;

namespace PortalBase.Application.Tests.Dobras;

/// <summary>Autorização de PLATAFORMA configurável (permitir/negar) para os testes de casos de uso de plataforma.</summary>
internal sealed class AutorizacaoPlataformaFake : IAutorizacaoPlataforma
{
    public bool Permitir { get; set; } = true;

    public MotivoNegacao Motivo { get; set; } = MotivoNegacao.SemPermissao;

    public int Chamadas { get; private set; }

    public ValueTask<ResultadoAutorizacao> AvaliarAsync(string permissaoGlobal, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Permitir ? ResultadoAutorizacao.Ok : ResultadoAutorizacao.Negar(Motivo));

    public ValueTask ExigirAsync(string permissaoGlobal, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        if (Permitir)
        {
            return ValueTask.CompletedTask;
        }

        throw Motivo switch
        {
            MotivoNegacao.NaoAutenticado => new NaoAutenticadoException(),
            MotivoNegacao.MfaRequerido => new MfaRequeridoException(),
            _ => new AutorizacaoNegadaException(),
        };
    }
}
