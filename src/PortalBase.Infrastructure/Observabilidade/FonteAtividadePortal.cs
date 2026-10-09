using System.Diagnostics;

namespace PortalBase.Infrastructure.Observabilidade;

/// <summary>
/// Fonte ÚNICA de atividades (spans) do Portal Base (Prompt 51, Fases 8/9) — <see cref="ActivitySource"/>
/// nomeada, registrada no tracing do Web e do Worker (<c>AddSource</c>). Cria spans para operações
/// relevantes (ex.: chamadas externas), com tags SEGURAS apenas (endpoint lógico, método, status,
/// duração). NUNCA carrega <c>Authorization</c>, token, segredo, connection string nem payload
/// como tag (regra 8/16; §9/§25). Um único ponto evita nomes de fonte espalhados pelo código.
/// </summary>
public static class FonteAtividadePortal
{
    /// <summary>Nome da fonte — deve ser registrado com <c>tracing.AddSource(FonteAtividadePortal.NomeFonte)</c>.</summary>
    public const string NomeFonte = "PortalBase";

    public static readonly ActivitySource Fonte = new(NomeFonte);
}
