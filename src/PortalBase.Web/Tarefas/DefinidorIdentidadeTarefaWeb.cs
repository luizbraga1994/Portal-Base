using System.Security.Claims;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Tarefas;
using PortalBase.Web.Seguranca;

namespace PortalBase.Web.Tarefas;

/// <summary>
/// Identidade da tarefa em segundo plano na Web (ARQ-006): o principal de QUEM PEDIU (só o identificador, gravado no
/// servidor no pedido) vai para o <see cref="PortadorPrincipalCircuito"/> do escopo da execução — o mesmo caminho do
/// contexto de segurança de um circuito. Sem MFA na identidade: operação que exigir step-up falha (fail-closed). A
/// execução nunca acontece dentro de uma requisição HTTP (o usuário da requisição teria precedência): se houver, falha.
/// </summary>
public sealed class DefinidorIdentidadeTarefaWeb : IDefinidorIdentidadeTarefa
{
    /// <summary>Tipo de autenticação da identidade da tarefa (aparece só no servidor).</summary>
    public const string TipoAutenticacao = "tarefa-segundo-plano";

    public void Definir(IServiceProvider escopo, Guid solicitanteId)
    {
        ArgumentNullException.ThrowIfNull(escopo);
        if (solicitanteId == Guid.Empty)
        {
            throw new NaoAutenticadoException();
        }

        if (escopo.GetService<IHttpContextAccessor>()?.HttpContext is not null)
        {
            throw new InvalidOperationException("Tarefa em segundo plano não pode rodar dentro de uma requisição HTTP.");
        }

        var identidade = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, solicitanteId.ToString())], TipoAutenticacao);
        escopo.GetRequiredService<PortadorPrincipalCircuito>().Definir(new ClaimsPrincipal(identidade));
    }
}
