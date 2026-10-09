using System.Reflection;

namespace PortalBase.Infrastructure.Producao;

/// <summary>
/// Identidade da IMPLANTAÇÃO/RELEASE em execução (Prompt 53, Fase 3/7) — versão, commit e timestamp de
/// build, mais ambiente e host. Serve para relacionar VERSION → COMMIT → BUILD → IMAGE e para expor a
/// versão de forma SEGURA no log de startup e no diagnóstico administrativo. NUNCA contém segredo/token/
/// connection string. O commit/build vêm de variáveis de ambiente injetadas pela imagem/CI
/// (<c>PORTALBASE_BUILD_*</c>); a versão cai para a <see cref="AssemblyInformationalVersionAttribute"/>.
/// </summary>
public sealed record InformacaoImplantacao(
    string Versao,
    string CommitSha,
    string BuildTimestamp,
    string Ambiente,
    string Host)
{
    /// <summary>Coleta a identidade da implantação do assembly de entrada + variáveis de ambiente.</summary>
    public static InformacaoImplantacao Coletar(string? ambiente)
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var versao = PrimeiroNaoVazio(
            Environment.GetEnvironmentVariable("PORTALBASE_BUILD_VERSION"),
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version?.ToString(),
            "desconhecida");

        var commit = PrimeiroNaoVazio(Environment.GetEnvironmentVariable("PORTALBASE_BUILD_SHA"), "desconhecido");
        var build = PrimeiroNaoVazio(Environment.GetEnvironmentVariable("PORTALBASE_BUILD_TIMESTAMP"), "desconhecido");

        return new InformacaoImplantacao(
            versao,
            commit,
            build,
            PrimeiroNaoVazio(ambiente, "desconhecido"),
            Environment.MachineName);
    }

    /// <summary>Linha compacta para o log de startup (sem segredo) — VERSION → COMMIT → BUILD → ENV.</summary>
    public string ParaLog() => $"versao={Versao} commit={CommitSha} build={BuildTimestamp} ambiente={Ambiente}";

    private static string PrimeiroNaoVazio(params string?[] candidatos)
    {
        foreach (var candidato in candidatos)
        {
            if (!string.IsNullOrWhiteSpace(candidato))
            {
                return candidato.Trim();
            }
        }

        return "desconhecida";
    }
}
