namespace PortalBase.Web.Seguranca;

/// <summary>
/// Limites de RATE LIMITING dos endpoints sensíveis (Prompt 68, Fases 18/24) — CONFIGURÁVEIS e centralizados,
/// nunca literais espalhados. Ligados à seção <c>Seguranca:RateLimit</c>. Os PADRÕES reproduzem exatamente o
/// comportamento anterior (login/MFA 10 por janela, upload 30, janela de 5 min), de modo que a ausência de
/// configuração NÃO altera a segurança vigente. Validado no startup (fail-fast): valores inválidos abortam a
/// composição, em vez de silenciosamente relaxar o limite.
/// </summary>
public sealed class OpcoesRateLimit
{
    public const string Secao = "Seguranca:RateLimit";

    /// <summary>Requisições de LOGIN permitidas por origem dentro da janela.</summary>
    public int LoginPorJanela { get; set; } = 10;

    /// <summary>Requisições de step-up MFA permitidas por origem dentro da janela.</summary>
    public int MfaPorJanela { get; set; } = 10;

    /// <summary>Pedidos de "Esqueci a senha" e redefinições permitidos por origem dentro da janela.</summary>
    public int RecuperacaoSenhaPorJanela { get; set; } = 5;

    /// <summary>Requisições de UPLOAD permitidas por origem dentro da janela.</summary>
    public int UploadPorJanela { get; set; } = 30;

    /// <summary>Tamanho da janela deslizante fixa, em minutos.</summary>
    public int JanelaMinutos { get; set; } = 5;

    public TimeSpan Janela => TimeSpan.FromMinutes(JanelaMinutos);

    /// <summary>Fail-fast: limites e janela têm de ser positivos (nunca desabilitar o limite por engano).</summary>
    public void Validar()
    {
        if (LoginPorJanela < 1 || MfaPorJanela < 1 || UploadPorJanela < 1 || RecuperacaoSenhaPorJanela < 1 || JanelaMinutos < 1)
        {
            throw new InvalidOperationException(
                $"Configuração '{Secao}' inválida: limites por janela e minutos precisam ser >= 1.");
        }
    }
}
