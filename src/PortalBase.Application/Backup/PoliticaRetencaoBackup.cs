using PortalBase.Domain.Backup;

namespace PortalBase.Application.Backup;

/// <summary>Periodicidade de um backup (Prompt 50, Fase 9) — governa a janela de retenção aplicada.</summary>
public enum PeriodicidadeBackup
{
    Diario = 1,
    Semanal = 2,
    Mensal = 3,
}

/// <summary>
/// Política de RETENÇÃO de backups (Prompt 50, Fase 9), DIRIGIDA POR CONFIGURAÇÃO — nada de períodos
/// arbitrários no código. Duas garantias inegociáveis:
/// (1) só descarta o que está FORA da janela de retenção;
/// (2) NUNCA apaga o único backup conhecido de um alvo (mesmo expirado) — a última linha de defesa.
/// </summary>
public sealed class PoliticaRetencaoBackup
{
    private readonly OpcoesBackup _opcoes;

    public PoliticaRetencaoBackup(OpcoesBackup opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        _opcoes = opcoes;
    }

    /// <summary>Data-limite de retenção de um backup recém-concluído, conforme a periodicidade.</summary>
    public DateTimeOffset CalcularRetencaoAte(PeriodicidadeBackup periodicidade, DateTimeOffset agoraUtc)
    {
        var dias = periodicidade switch
        {
            PeriodicidadeBackup.Diario => _opcoes.RetencaoDiariaDias,
            PeriodicidadeBackup.Semanal => _opcoes.RetencaoSemanalDias,
            PeriodicidadeBackup.Mensal => _opcoes.RetencaoMensalDias,
            _ => _opcoes.RetencaoDiariaDias,
        };

        return agoraUtc.AddDays(Math.Max(1, dias));
    }

    /// <summary>
    /// Pode DESCARTAR este backup? Somente se expirado (fora da retenção) E existir mais de um backup
    /// concluído do MESMO alvo (nunca apagar o único — Fase 9). <paramref name="totalConcluidosDoAlvo"/>
    /// é a contagem atual de backups concluídos daquele tenant/tipo.
    /// </summary>
    public static bool PodeDescartar(RegistroBackup registro, int totalConcluidosDoAlvo, DateTimeOffset agoraUtc)
    {
        ArgumentNullException.ThrowIfNull(registro);

        if (registro.DentroDaRetencao(agoraUtc))
        {
            return false; // ainda dentro da janela
        }

        // Nunca remover o último backup disponível de um alvo, mesmo expirado.
        return totalConcluidosDoAlvo > 1;
    }
}
