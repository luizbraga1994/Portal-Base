using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.DependencyInjection;
using PortalBase.Infrastructure.DependencyInjection;

namespace PortalBase.AcessoLocal;

/// <summary>
/// Fluxo do executável: argumentos → trava de ambiente → configuração da Web (mesmos user-secrets) → trava de
/// configuração → dados faltantes → composição de DI de produção → <see cref="CriadorAcessoLocal"/>.
/// Separado de <c>Program</c> para que as recusas sejam testáveis sem processo nem console.
/// </summary>
public static class ProgramaAcessoLocal
{
    /// <summary>UserSecretsId da Web: o comando lê exatamente a configuração que a Web vai usar.</summary>
    public const string UserSecretsIdWeb = "portalbase-web";

    public static async Task<int> ExecutarAsync(
        IReadOnlyList<string> args,
        ITerminal terminal,
        Func<string, string?> variavelAmbiente,
        Func<IConfiguration> carregarConfiguracao,
        string usuarioSistema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(variavelAmbiente);
        ArgumentNullException.ThrowIfNull(carregarConfiguracao);

        var (argumentos, erroArgumentos) = ArgumentosAcessoLocal.Interpretar(args);
        if (argumentos is null)
        {
            terminal.Erro(erroArgumentos!);
            return (int)ResultadoAcessoLocal.EntradaInvalida;
        }

        if (argumentos.Ajuda)
        {
            terminal.Escrever(ArgumentosAcessoLocal.Uso);
            return (int)ResultadoAcessoLocal.Sucesso;
        }

        // 1ª trava: antes de carregar QUALQUER configuração.
        var dotnetEnvironment = variavelAmbiente("DOTNET_ENVIRONMENT");
        var aspnetCoreEnvironment = variavelAmbiente("ASPNETCORE_ENVIRONMENT");
        var recusaAmbiente = GuardaAmbienteLocal.VerificarAmbiente(dotnetEnvironment, aspnetCoreEnvironment);
        if (recusaAmbiente is not null)
        {
            terminal.Erro(recusaAmbiente);
            return (int)ResultadoAcessoLocal.Recusado;
        }

        var configuracao = carregarConfiguracao();

        // 2ª trava: provedor local de segredos e MySQL desta máquina.
        var recusaConfiguracao = GuardaAmbienteLocal.VerificarConfiguracao(configuracao);
        if (recusaConfiguracao is not null)
        {
            terminal.Erro(recusaConfiguracao);
            return (int)ResultadoAcessoLocal.Recusado;
        }

        var entrada = Completar(argumentos, terminal);
        if (entrada is null)
        {
            return (int)ResultadoAcessoLocal.EntradaInvalida;
        }

        var servicos = new ServiceCollection();
        servicos.AddSingleton<IConfiguration>(configuracao);
        servicos.AddLogging(); // sem provedores: nada vai para console/arquivo além do diálogo do terminal
        servicos.AddApplication();
        servicos.AddInfrastructure(configuracao, conexaoObrigatoria: true, producao: false);

        await using var provedor = servicos.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var criador = new CriadorAcessoLocal(
            provedor.GetRequiredService<IServiceScopeFactory>(), terminal, CriadorAcessoLocal.AtorPara(usuarioSistema));
        var resultado = await criador.ExecutarAsync(entrada, cancellationToken).ConfigureAwait(false);
        return (int)resultado;
    }

    /// <summary>
    /// Mesma configuração que a Web lê em Development: appsettings.json + appsettings.Development.json (copiados da
    /// Web) + user-secrets da Web + variáveis de ambiente (nesta ordem de precedência crescente).
    /// </summary>
    public static IConfiguration CarregarConfiguracao(string diretorioBase)
        => new ConfigurationBuilder()
            .SetBasePath(diretorioBase)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{GuardaAmbienteLocal.AmbientePermitido}.json", optional: true)
            .AddUserSecrets(UserSecretsIdWeb)
            .AddEnvironmentVariables()
            .Build();

    /// <summary>
    /// Pergunta CNPJ e login se faltarem (só com terminal interativo) e lê a senha do stdin se pedido. Razão social,
    /// nome e e-mail só são perguntados depois, pelo <see cref="CriadorAcessoLocal"/>, e só se forem necessários.
    /// </summary>
    public static EntradaAcessoLocal? Completar(ArgumentosAcessoLocal argumentos, ITerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(argumentos);
        ArgumentNullException.ThrowIfNull(terminal);

        var cnpj = Obter(argumentos.Cnpj, "CNPJ do contribuinte (14 dígitos)", "--cnpj", terminal);
        var login = cnpj is null ? null : Obter(argumentos.Login, "Login do usuário", "--login", terminal);
        if (login is null)
        {
            return null;
        }

        string? senha = null;
        if (argumentos.SenhaStdin)
        {
            if (terminal.Interativo)
            {
                terminal.Erro("--senha-stdin exige a senha redirecionada para a entrada padrão (sem terminal interativo).");
                return null;
            }

            senha = terminal.LerLinha();
            if (senha is null)
            {
                terminal.Erro("--senha-stdin: a entrada padrão terminou sem nenhuma linha.");
                return null;
            }

            if (argumentos.SenhaStdinBase64)
            {
                senha = DecodificarSenhaBase64(senha);
                if (senha is null)
                {
                    terminal.Erro("--senha-stdin-base64: a linha recebida não é Base64 de um texto UTF-8 válido.");
                    return null;
                }
            }
        }

        return new EntradaAcessoLocal(cnpj!, login, argumentos.Papeis)
        {
            Tenant = argumentos.Tenant,
            RazaoSocial = argumentos.RazaoSocial,
            Nome = argumentos.Nome,
            Email = argumentos.Email,
            RedefinirSenha = argumentos.RedefinirSenha,
            AtivarMfa = argumentos.ComMfa,
            RemoverMfa = argumentos.RemoverMfa,
            Senha = senha,
        };
    }

    /// <summary>
    /// Base64 → bytes → UTF-8 ESTRITO (byte inválido é rejeitado, nunca vira '?'). Devolve <c>null</c> se inválido; o
    /// conteúdo nunca é ecoado na mensagem de erro.
    /// </summary>
    public static string? DecodificarSenhaBase64(string? linha)
    {
        if (string.IsNullOrWhiteSpace(linha))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(linha.Trim());
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null; // DecoderFallbackException (UTF-8 inválido) deriva de ArgumentException
        }
    }

    private static string? Obter(string? valor, string rotulo, string opcao, ITerminal terminal)
    {
        if (!string.IsNullOrWhiteSpace(valor))
        {
            return valor;
        }

        if (!terminal.Interativo)
        {
            terminal.Erro($"Informe {opcao} (sem terminal interativo não há como perguntar).");
            return null;
        }

        var resposta = terminal.Perguntar(rotulo);
        if (string.IsNullOrWhiteSpace(resposta))
        {
            terminal.Erro($"{rotulo}: valor obrigatório.");
            return null;
        }

        return resposta;
    }
}
