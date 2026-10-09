namespace PortalBase.Application.Producao;

/// <summary>
/// Funcionalidade crítica cujo rollout é controlado por configuração (Prompt 33, seções 41/43).
/// Obrigações com cronograma de fases não embutem datas na lógica — a disponibilidade é dirigida por
/// configuração versionada e auditada. Substitua os valores pelos do domínio do seu portal.
/// </summary>
public enum FuncionalidadeCritica
{
    /// <summary>Envio de arquivos (upload) — exemplo do template.</summary>
    EnvioArquivos = 1,

    /// <summary>Integração com uma API externa — exemplo do template.</summary>
    IntegracaoExterna = 2,
}

/// <summary>
/// Regra de disponibilidade de uma funcionalidade (Prompt 33, seção 43). As datas de vigência vêm da
/// CONFIGURAÇÃO (nunca hardcoded na lógica). <see cref="Habilitada"/> é o interruptor
/// principal; a janela [<see cref="VigenteDeUtc"/>, <see cref="VigenteAteUtc"/>] é opcional.
/// </summary>
public sealed record RegraDisponibilidade(
    FuncionalidadeCritica Funcionalidade,
    bool Habilitada,
    DateTimeOffset? VigenteDeUtc = null,
    DateTimeOffset? VigenteAteUtc = null);

/// <summary>
/// Avalia se uma funcionalidade crítica está disponível AGORA (Prompt 33, seções 41/43),
/// de forma <b>fail-closed</b>: sem regra explícita, ou fora da janela de vigência configurada, a
/// funcionalidade é considerada INDISPONÍVEL. Nunca há data embutida na lógica — tudo vem da
/// configuração (versionada/auditada por deploy). Determinístico: recebe o instante como parâmetro.
/// </summary>
public static class AvaliadorDisponibilidade
{
    public static bool Disponivel(
        IEnumerable<RegraDisponibilidade> regras, FuncionalidadeCritica funcionalidade, DateTimeOffset agoraUtc)
    {
        ArgumentNullException.ThrowIfNull(regras);

        var regra = regras.LastOrDefault(r => r.Funcionalidade == funcionalidade);
        if (regra is null || !regra.Habilitada)
        {
            return false; // fail-closed: sem regra ou desabilitada explicitamente.
        }

        if (regra.VigenteDeUtc is { } de && agoraUtc < de)
        {
            return false; // ainda não vigente.
        }

        if (regra.VigenteAteUtc is { } ate && agoraUtc >= ate)
        {
            return false; // vigência encerrada.
        }

        return true;
    }
}
