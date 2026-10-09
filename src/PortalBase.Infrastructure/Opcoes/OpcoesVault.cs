namespace PortalBase.Infrastructure.Opcoes;

/// <summary>
/// Configuração do cofre gerenciado (Prompt 06). Contém SOMENTE dados NÃO sensíveis:
/// endpoint, modo, ambiente e TTL de cache (seção 26). NUNCA senha, client secret ou chave.
/// A autenticação é por identidade gerenciada (sem segredo estático na aplicação).
/// </summary>
public sealed class OpcoesVault
{
    public const string Secao = "Vault";

    public const string ModoKeyVault = "KeyVault";
    public const string ModoCofreWindows = "CofreWindows";
    public const string ModoConfiguracaoLocal = "ConfiguracaoLocal";

    /// <summary>
    /// <c>KeyVault</c> (Azure), <c>CofreWindows</c> (cofre local cifrado pelo Windows — servidor próprio/IIS) ou
    /// <c>ConfiguracaoLocal</c> (apenas Dev/Test).
    /// </summary>
    public string Modo { get; set; } = ModoConfiguracaoLocal;

    /// <summary>Pasta privada do cofre local (modo <c>CofreWindows</c>), com ACL só para o pool do IIS, o Worker e administradores.</summary>
    public string? DiretorioCofreWindows { get; set; }

    /// <summary>Endpoint do cofre (ex.: https://meucofre.vault.azure.net/). Obrigatório em modo KeyVault.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Nome lógico do ambiente para compor nomes de segredo (dev/test/prod).</summary>
    public string Ambiente { get; set; } = "dev";

    /// <summary>TTL do cache em memória (segundos). Curto e justificável (seção 11).</summary>
    public int CacheTtlSegundos { get; set; } = 300;

    /// <summary>
    /// SÓ Development (modo <c>ConfiguracaoLocal</c>): arquivo de user-secrets onde os segredos DEFINIDOS pela tela
    /// (senha do SMTP, segredos de integração) são guardados para sobreviver ao reinício — fora do repositório, no perfil do
    /// usuário. A Web preenche com o próprio user-secrets em Development; vazio = só memória (Test). Ignorado com o
    /// cofre gerenciado (Produção).
    /// </summary>
    public string? ArquivoSegredosLocal { get; set; }
}
