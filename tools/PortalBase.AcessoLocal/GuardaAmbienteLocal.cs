using System.Net;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace PortalBase.AcessoLocal;

/// <summary>
/// Trava do comando (fail-closed): só roda em <b>Development</b>, com o provedor LOCAL de segredos, com a
/// configuração do <c>scripts/configurar-ambiente-local.ps1</c> e contra um MySQL na PRÓPRIA máquina. Fora disso,
/// acesso é criado pelos fluxos administrativos autorizados e auditados — nunca por esta ferramenta.
/// </summary>
public static class GuardaAmbienteLocal
{
    public const string AmbientePermitido = "Development";

    /// <summary>
    /// Ambiente efetivo a partir de <c>DOTNET_ENVIRONMENT</c> e <c>ASPNETCORE_ENVIRONMENT</c>. Se os dois estiverem
    /// definidos e divergirem, devolve <c>null</c> (ambiente ambíguo ⇒ recusa, nunca "o mais permissivo").
    /// </summary>
    public static string? AmbienteEfetivo(string? dotnetEnvironment, string? aspnetCoreEnvironment)
    {
        var dotnet = string.IsNullOrWhiteSpace(dotnetEnvironment) ? null : dotnetEnvironment.Trim();
        var aspnet = string.IsNullOrWhiteSpace(aspnetCoreEnvironment) ? null : aspnetCoreEnvironment.Trim();

        if (dotnet is not null && aspnet is not null && !string.Equals(dotnet, aspnet, StringComparison.Ordinal))
        {
            return null;
        }

        return dotnet ?? aspnet;
    }

    /// <summary>Recusa (motivo) se o ambiente não for Development; <c>null</c> quando é.</summary>
    public static string? VerificarAmbiente(string? dotnetEnvironment, string? aspnetCoreEnvironment)
    {
        var ambiente = AmbienteEfetivo(dotnetEnvironment, aspnetCoreEnvironment);
        if (string.Equals(ambiente, AmbientePermitido, StringComparison.Ordinal))
        {
            return null;
        }

        var ambiguo = ambiente is null && dotnetEnvironment is not null && aspnetCoreEnvironment is not null;
        var descricao = ambiguo
            ? $"ambíguo: DOTNET_ENVIRONMENT='{dotnetEnvironment}' e ASPNETCORE_ENVIRONMENT='{aspnetCoreEnvironment}'"
            : $"'{ambiente ?? "não definido"}'";
        var recusa = $"Recusado: este comando só roda em {AmbientePermitido} (ambiente atual: {descricao}). "
            + "Fora do desenvolvimento local, acessos são criados pelos fluxos administrativos autorizados e auditados.";

        // Ambíguo numa máquina de desenvolvimento costuma ser uma variável esquecida no sistema: diz qual remover. A trava
        // continua a mesma — nada aqui escolhe um dos dois valores.
        var divergente = string.Equals(dotnetEnvironment?.Trim(), AmbientePermitido, StringComparison.Ordinal)
            ? "ASPNETCORE_ENVIRONMENT"
            : "DOTNET_ENVIRONMENT";
        return ambiguo
            ? recusa + $" Se esta é a sua máquina de desenvolvimento, remova a variável {divergente} "
                + $"(PowerShell: Remove-Item Env:{divergente}; de vez: [Environment]::SetEnvironmentVariable('{divergente}', $null, 'User')) "
                + "e rode de novo."
            : recusa;
    }

    /// <summary>Recusa (motivo) se a configuração não for a de um desenvolvimento local; <c>null</c> quando é.</summary>
    public static string? VerificarConfiguracao(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        if (!string.Equals(configuracao["Vault:Modo"], "ConfiguracaoLocal", StringComparison.Ordinal))
        {
            return "Recusado: Vault:Modo precisa ser 'ConfiguracaoLocal' (provedor local de segredos do desenvolvimento). "
                + "Cofre gerenciado indica um ambiente que não é local.";
        }

        var catalogo = configuracao.GetConnectionString("Catalogo");
        if (string.IsNullOrWhiteSpace(catalogo))
        {
            return "Configuração ausente (ConnectionStrings:Catalogo). Rode antes: .\\scripts\\configurar-ambiente-local.ps1 -AplicarMigracoesCatalogo";
        }

        string servidorCatalogo;
        try
        {
            servidorCatalogo = new MySqlConnectionStringBuilder(catalogo).Server;
        }
        catch (ArgumentException)
        {
            // A mensagem do parser pode citar trechos da connection string: não é repassada (regra 16).
            return "ConnectionStrings:Catalogo não é uma connection string MySQL válida. Rode de novo o configurar-ambiente-local.ps1.";
        }

        if (!EhServidorLocal(servidorCatalogo))
        {
            return $"Recusado: o catálogo aponta para '{servidorCatalogo}', que não é esta máquina. "
                + "O comando só cria acesso em MySQL local (localhost/127.0.0.1/::1).";
        }

        // É neste servidor que o provisionamento cria a base tenant_* do contribuinte.
        var servidorTenant = configuracao["BancoTenant:Servidor"];
        if (string.IsNullOrWhiteSpace(servidorTenant) || !EhServidorLocal(servidorTenant))
        {
            return $"Recusado: BancoTenant:Servidor ('{servidorTenant ?? "não definido"}') precisa ser esta máquina "
                + "(localhost/127.0.0.1/::1).";
        }

        return null;
    }

    /// <summary>Somente loopback: <c>localhost</c> ou IP de loopback. Lista de hosts exige que TODOS sejam locais.</summary>
    public static bool EhServidorLocal(string? servidor)
    {
        if (string.IsNullOrWhiteSpace(servidor))
        {
            return false;
        }

        foreach (var parte in servidor.Split(',', StringSplitOptions.TrimEntries))
        {
            if (string.Equals(parte, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ip = parte.StartsWith('[') && parte.EndsWith(']') ? parte[1..^1] : parte;
            if (!IPAddress.TryParse(ip, out var endereco) || !IPAddress.IsLoopback(endereco))
            {
                return false;
            }
        }

        return true;
    }
}
