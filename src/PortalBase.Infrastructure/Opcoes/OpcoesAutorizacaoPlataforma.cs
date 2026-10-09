namespace PortalBase.Infrastructure.Opcoes;

/// <summary>
/// Política de autorização de PLATAFORMA (Prompt 10.1): quem pode administrar o CATÁLOGO FISCAL
/// GLOBAL (importar/ativar versões oficiais). É deliberadamente SEPARADA da autorização de tenant —
/// nenhum papel/permissão de tenant concede este acesso (regras 3, 4, 17). Deny-by-default: sem
/// administradores configurados, TODA operação de catálogo global é negada (fail-closed).
///
/// Os identificadores abaixo são IDs de principal (não são segredos — regra 7 não se aplica): são
/// política de autorização, equivalente a uma atribuição de papel. NUNCA colocar senha/token/chave.
/// </summary>
public sealed class OpcoesAutorizacaoPlataforma
{
    public const string Secao = "AutorizacaoPlataforma";

    /// <summary>
    /// IDs (GUID) dos principais autorizados como ADMINISTRADORES DE PLATAFORMA. Vazio ⇒ deny-all.
    /// </summary>
    public IList<string> AdministradoresPlataforma { get; } = [];

    /// <summary>
    /// Exigir segundo fator satisfeito para operações de plataforma (alto impacto). Padrão: true.
    /// </summary>
    public bool ExigirMfa { get; set; } = true;
}
