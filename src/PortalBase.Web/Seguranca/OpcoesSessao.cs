namespace PortalBase.Web.Seguranca;

/// <summary>Sessão por cookie (seção <c>Seguranca:Sessao</c>).</summary>
public sealed class OpcoesSessao
{
    public const string Secao = "Seguranca:Sessao";

    /// <summary>
    /// De quantos em quantos segundos (no máximo) cada sessão é conferida no catálogo — senha trocada, usuário inativo
    /// ou bloqueado encerram a sessão nesse prazo. 0 = a cada requisição. Padrão: 60.
    /// </summary>
    public int RevalidacaoSegundos { get; set; } = 60;

    public TimeSpan Intervalo => TimeSpan.FromSeconds(RevalidacaoSegundos);

    /// <summary>Fail-fast: entre 0 e 3600 segundos (nunca desligar a revalidação por engano).</summary>
    public void Validar()
    {
        if (RevalidacaoSegundos is < 0 or > 3600)
        {
            throw new InvalidOperationException($"Configuração '{Secao}:RevalidacaoSegundos' inválida: use de 0 a 3600.");
        }
    }
}
