using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortalBase.Application.Abstracoes;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Segredos;

/// <summary>
/// Composição do provedor de segredos por AMBIENTE (Prompt 06, seções 5-7). Regras:
/// <list type="bullet">
/// <item>Produção EXIGE cofre: gerenciado (<c>Vault:Modo=KeyVault</c>) ou o cofre local cifrado pelo Windows
/// (<c>Vault:Modo=CofreWindows</c>, servidor próprio/IIS) — o provedor por configuração de Dev/Test é PROIBIDO em produção
/// (fail-fast no startup, sem fallback silencioso — seção 7).</item>
/// <item>Modo KeyVault: autentica por identidade gerenciada (sem client secret estático) e
/// aplica cache com TTL curto.</item>
/// <item>Dev/Test: provedor por configuração (variáveis/ambiente), explicitamente NÃO-produção.</item>
/// </list>
/// </summary>
public static class ConfiguracaoSegredos
{
    public static IServiceCollection AddSegredos(this IServiceCollection services, IConfiguration configuration, bool producao)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OpcoesVault>(configuration.GetSection(OpcoesVault.Secao));

        var opcoes = new OpcoesVault();
        configuration.GetSection(OpcoesVault.Secao).Bind(opcoes);
        var usaCofre = string.Equals(opcoes.Modo, OpcoesVault.ModoKeyVault, StringComparison.OrdinalIgnoreCase);
        var usaCofreWindows = string.Equals(opcoes.Modo, OpcoesVault.ModoCofreWindows, StringComparison.OrdinalIgnoreCase);

        // Fail-fast: em Produção, só cofre é aceito (seção 7 — sem downgrade).
        if (producao && !usaCofre && !usaCofreWindows)
        {
            throw new InvalidOperationException(
                "Em Produção o gerenciamento de segredos exige Vault:Modo=KeyVault (Azure) ou Vault:Modo=CofreWindows (cofre local do Windows). " +
                "O provedor por configuração é permitido apenas em Development/Test.");
        }

        if (usaCofre)
        {
            RegistrarCofreGerenciado(services, opcoes);
        }
        else if (usaCofreWindows)
        {
            RegistrarCofreWindows(services, opcoes);
        }
        else
        {
            // Provedor Dev/Test (NÃO-produção): resolve 'Segredos:<ref>' da configuração/ambiente. Em Development, o que
            // a tela define é guardado também no user-secrets indicado (sobrevive ao reinício); em Test, só em memória.
            var arquivoLocal = producao ? null : opcoes.ArquivoSegredosLocal;
            services.AddSingleton<IProvedorSegredos>(sp => new ProvedorSegredosConfiguracao(
                sp.GetRequiredService<IConfiguration>(), arquivoLocal));
        }

        // Escrita de segredo (rotação/definição em runtime — ex.: senha do SMTP). Em AMBOS os modos o provedor
        // registrado implementa IEscritorSegredos (KeyVault: escreve no cofre; Dev/Test: overlay em memória).
        // O valor vai SOMENTE para o cofre, NUNCA para banco/log (regra 7).
        services.AddSingleton<IEscritorSegredos>(sp => (IEscritorSegredos)sp.GetRequiredService<IProvedorSegredos>());

        return services;
    }

    private static void RegistrarCofreWindows(IServiceCollection services, OpcoesVault opcoes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Vault:Modo=CofreWindows só funciona no Windows (DPAPI da máquina).");
        }

        if (string.IsNullOrWhiteSpace(opcoes.DiretorioCofreWindows))
        {
            throw new InvalidOperationException("Vault:DiretorioCofreWindows é obrigatório quando Vault:Modo=CofreWindows.");
        }

        // A pasta precisa existir já no arranque (fail-fast): o provedor confere ao ser criado.
        var cofre = new ProvedorSegredosCofreWindows(opcoes.DiretorioCofreWindows, new ProtetorDpapiMaquina());
        services.AddSingleton(cofre);
        services.AddSingleton<IProvedorSegredos>(sp => new ProvedorSegredosCache(
            cofre, sp.GetRequiredService<IRelogio>(), TimeSpan.FromSeconds(opcoes.CacheTtlSegundos), sp.GetService<ILogger<ProvedorSegredosCache>>()));
    }

    private static void RegistrarCofreGerenciado(IServiceCollection services, OpcoesVault opcoes)
    {
        if (string.IsNullOrWhiteSpace(opcoes.Endpoint))
        {
            throw new InvalidOperationException("Vault:Endpoint é obrigatório quando Vault:Modo=KeyVault.");
        }

        var clientOptions = new SecretClientOptions();
        clientOptions.Retry.MaxRetries = 3;                          // retry controlado (seção 30)
        clientOptions.Retry.Mode = RetryMode.Exponential;
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);

        // Identidade GERENCIADA/Workload Identity — nenhum client secret estático (seções 22, 23).
        var credencial = new DefaultAzureCredential();
        var cliente = new SecretClient(new Uri(opcoes.Endpoint), credencial, clientOptions);
        services.AddSingleton(cliente);
        services.AddSingleton<ProvedorSegredosKeyVault>();

        // Cache com TTL curto por cima do cofre (seção 11).
        services.AddSingleton<IProvedorSegredos>(sp => new ProvedorSegredosCache(
            sp.GetRequiredService<ProvedorSegredosKeyVault>(),
            sp.GetRequiredService<IRelogio>(),
            TimeSpan.FromSeconds(opcoes.CacheTtlSegundos),
            sp.GetService<ILogger<ProvedorSegredosCache>>()));
    }
}
