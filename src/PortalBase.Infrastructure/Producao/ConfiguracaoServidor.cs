using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using PortalBase.Application.Producao;

namespace PortalBase.Infrastructure.Producao;

/// <summary>
/// Configuração do SERVIDOR próprio (Windows/IIS, sem Azure): um arquivo JSON por host em
/// <c>%ProgramData%\PortalBase</c> (<c>PortalBase.Web.json</c> e <c>PortalBase.Worker.json</c>), gravado pelo
/// <c>scripts/configurar-iis.ps1</c>. Vale igual para o site no IIS, para o serviço do Worker e para o <c>.exe</c> aberto
/// à mão — sem depender das variáveis do pool. Só em ambiente produtivo; variáveis de ambiente e linha de comando
/// continuam prevalecendo sobre ele.
/// Regra 7: o arquivo NÃO pode ter segredo — senha numa string de conexão ou chave com nome de segredo (Senha,
/// Password, Secret, Token...) aborta a inicialização; ali ficam só as REFERÊNCIAS ao cofre (<c>...Ref</c>).
/// </summary>
public static partial class ConfiguracaoServidor
{
    /// <summary>Troca a pasta (padrão <c>%ProgramData%\PortalBase</c>), ex.: em testes.</summary>
    public const string VariavelDiretorio = "PORTALBASE_DIRETORIO_CONFIGURACAO";

    public static string Diretorio()
    {
        var informado = Environment.GetEnvironmentVariable(VariavelDiretorio);
        return string.IsNullOrWhiteSpace(informado)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PortalBase")
            : informado.Trim();
    }

    public static string Caminho(string nomeAplicacao, string? diretorio = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeAplicacao);
        return Path.Combine(string.IsNullOrWhiteSpace(diretorio) ? Diretorio() : diretorio, nomeAplicacao + ".json");
    }

    /// <summary>Complemento da mensagem da pré-checagem de produção: onde a configuração deveria estar.</summary>
    public static string Dica(string nomeAplicacao)
        => $" Esperada em variáveis de ambiente ou no arquivo {Caminho(nomeAplicacao)}. Em servidor Windows (IIS), rode " +
           "scripts\\configurar-iis.ps1 como administrador: ele grava esse arquivo (lido pelo site, pelo serviço do Worker e " +
           "pelo .exe aberto num PowerShell de administrador).";

    /// <summary>
    /// Em ambiente produtivo e com o arquivo existente, confere que ele não tem segredo e o acrescenta DEPOIS dos
    /// appsettings (prevalece sobre eles) e ANTES das variáveis de ambiente gerais (que prevalecem sobre ele). Retorna o caminho usado, ou <c>null</c> se nada foi acrescentado.
    /// <paramref name="diretorio"/> substitui a pasta padrão (testes).
    /// </summary>
    public static string? Adicionar(IConfigurationBuilder configuracao, string? nomeAmbiente, string nomeAplicacao, string? diretorio = null)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        if (!ClassificadorAmbiente.EhProdutivo(ClassificadorAmbiente.Classificar(nomeAmbiente)))
        {
            return null;
        }

        var caminho = Caminho(nomeAplicacao, diretorio);
        if (!File.Exists(caminho))
        {
            return null;
        }

        Validar(caminho);

        var fonte = new JsonConfigurationSource { Path = caminho, Optional = false, ReloadOnChange = false };
        fonte.ResolveFileProvider();
        // Logo DEPOIS do último arquivo de configuração (appsettings.json, appsettings.Production.json): o arquivo do
        // servidor vale mais que eles — o "Catalogo": "" de exemplo do appsettings publicado não pode apagar a string de
        // conexão. As variáveis de ambiente gerais e a linha de comando vêm depois e continuam valendo mais. (As variáveis
        // com prefixo DOTNET_/ASPNETCORE_, da configuração do host, ficam antes dos appsettings.) Sem arquivo de
        // configuração na lista: antes da última fonte de variáveis de ambiente.
        var posicao = -1;
        for (var i = configuracao.Sources.Count - 1; i >= 0 && posicao < 0; i--)
        {
            if (configuracao.Sources[i] is FileConfigurationSource)
            {
                posicao = i + 1;
            }
        }

        for (var i = configuracao.Sources.Count - 1; i >= 0 && posicao < 0; i--)
        {
            if (configuracao.Sources[i] is EnvironmentVariablesConfigurationSource)
            {
                posicao = i;
            }
        }

        if (posicao < 0 || posicao >= configuracao.Sources.Count)
        {
            configuracao.Sources.Add(fonte);
        }
        else
        {
            configuracao.Sources.Insert(posicao, fonte);
        }

        return caminho;
    }

    /// <summary>
    /// Lê o arquivo e recusa (fail-fast, sem mostrar valores) JSON inválido, falta de permissão ou qualquer segredo.
    /// </summary>
    public static void Validar(string caminho)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caminho);
        IConfigurationRoot conteudo;
        try
        {
            conteudo = new ConfigurationBuilder().AddJsonFile(caminho, optional: false, reloadOnChange: false).Build();
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Sem permissão para ler {caminho}. Rode pelo IIS/serviço ou abra o PowerShell como administrador.");
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException(
                $"O arquivo {caminho} não é um JSON válido. Rode de novo o scripts\\configurar-iis.ps1.");
        }

        foreach (var (chave, valor) in conteudo.AsEnumerable())
        {
            if (valor is not null && EhSegredo(chave, valor))
            {
                throw new InvalidOperationException(
                    $"O arquivo {caminho} não pode conter segredo (chave '{chave}'). Guarde o valor no cofre e deixe no " +
                    "arquivo só a referência (ex.: BancoCatalogo:SenhaRef).");
            }
        }
    }

    /// <summary>Senha em string de conexão, ou chave cujo último nome é de segredo (as referências terminam em "Ref").</summary>
    public static bool EhSegredo(string chave, string valor)
    {
        ArgumentNullException.ThrowIfNull(chave);
        ArgumentNullException.ThrowIfNull(valor);
        var ultimo = chave[(chave.LastIndexOf(':') + 1)..];
        return NomeDeSegredo().IsMatch(ultimo) || SenhaEmConexao().IsMatch(valor);
    }

    [GeneratedRegex(@"^(senha|password|pwd|secret|clientsecret|token|apikey|chave|privatekey)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NomeDeSegredo();

    [GeneratedRegex(@"(^|;)\s*(password|pwd)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SenhaEmConexao();
}
