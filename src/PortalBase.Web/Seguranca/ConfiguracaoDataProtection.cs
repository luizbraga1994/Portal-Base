using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace PortalBase.Web.Seguranca;

/// <summary>
/// Data Protection (Prompt 11 — segurança operacional). O chaveiro do ASP.NET Core protege
/// artefatos TRANSITÓRIOS de sessão: o cookie de autenticação (<c>portalbase.auth</c>) e o token
/// antiforgery. O padrão do framework deriva o chaveiro do caminho de conteúdo e o mantém EFÊMERO
/// em contêiner — em produção com rolling deploy / múltiplas réplicas isso deslogaria o usuário a
/// cada reinício e invalidaria cookies emitidos por outra réplica. Endurecimento, sem dependência
/// externa nova e sem alterar o comportamento de desenvolvimento:
/// <list type="bullet">
///   <item>Nome de aplicação ESTÁVEL — discriminador de propósito consistente entre réplicas e
///   caminhos, para que o chaveiro seja o mesmo em todas as instâncias do host.</item>
///   <item>Persistência do chaveiro em VOLUME quando <c>DataProtection:CaminhoChaves</c> está
///   definido (produção); ausente ⇒ padrão do framework (desenvolvimento). Falha FECHADA se o
///   caminho for inutilizável — um chaveiro não persistente em produção é defeito operacional,
///   nunca um fallback silencioso (regras 1/10).</item>
/// </list>
/// Segredos SENSÍVEIS em repouso (segredo TOTP do MFA, backups) NÃO usam este chaveiro: são
/// cifrados por AES-GCM com chave do cofre (<c>ProtetorSegredoMfaAesGcm</c> / P06, P25, P50).
/// Este chaveiro cobre apenas tokens de sessão de curta duração.
/// </summary>
public static class ConfiguracaoDataProtection
{
    /// <summary>Discriminador de propósito estável entre réplicas do host Web.</summary>
    public const string NomeAplicacao = "PortalBase.Web";

    /// <summary>Chave de configuração do diretório persistente do chaveiro (volume em produção).</summary>
    public const string ChaveCaminho = "DataProtection:CaminhoChaves";

    private const string NomeSondaEscrita = ".portalbase-dp-probe";

    public static IServiceCollection AddDataProtectionEndurecido(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var construtor = services.AddDataProtection().SetApplicationName(NomeAplicacao);

        var caminho = configuration[ChaveCaminho];
        if (!string.IsNullOrWhiteSpace(caminho))
        {
            construtor.PersistKeysToFileSystem(PrepararDiretorio(caminho));
            if (OperatingSystem.IsWindows())
            {
                // Servidor Windows (IIS): o chaveiro fica cifrado em disco pelo DPAPI da máquina.
                construtor.ProtectKeysWithDpapi(protectToLocalMachine: true);
            }
        }

        return services;
    }

    /// <summary>
    /// Cria e valida (prova de escrita) o diretório do chaveiro. Falha FECHADA se não for utilizável:
    /// persistir o chaveiro em produção é requisito, não um caminho feliz opcional. A mensagem cita a
    /// CHAVE de configuração, nunca o valor bruto, e é de startup (aborta o boot, não vai ao usuário).
    /// </summary>
    private static DirectoryInfo PrepararDiretorio(string caminho)
    {
        try
        {
            var diretorio = Directory.CreateDirectory(caminho);
            var sonda = Path.Combine(diretorio.FullName, NomeSondaEscrita);
            File.WriteAllText(sonda, string.Empty);
            File.Delete(sonda);
            return diretorio;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Diretório de chaves do Data Protection ('{ChaveCaminho}') inutilizável. " +
                "Configure um volume persistente e gravável para produção.", ex);
        }
    }
}
