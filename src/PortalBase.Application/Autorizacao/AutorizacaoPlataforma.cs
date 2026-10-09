namespace PortalBase.Application.Autorizacao;

/// <summary>
/// Autorização de operações de PLATAFORMA (globais, não vinculadas a um tenant) — Prompt 10.1. É o
/// contraponto explícito de <see cref="IServicoAutorizacao"/> (que é sempre tenant-escopada): governa
/// ações sobre recursos GLOBAIS compartilhados por todos os contribuintes, como o versionamento do
/// CATÁLOGO FISCAL oficial da DeRE. Deny-by-default: exige principal autenticado + papel de
/// administrador de PLATAFORMA + (para alto impacto) segundo fator satisfeito. NUNCA deriva a decisão
/// de um identificador de tenant, de papel de tenant ou de qualquer dado do cliente (regras 3, 4, 15).
/// </summary>
public interface IAutorizacaoPlataforma
{
    /// <summary>Avalia (sem lançar) se o principal atual pode executar a operação global indicada.</summary>
    ValueTask<ResultadoAutorizacao> AvaliarAsync(string permissaoGlobal, CancellationToken cancellationToken = default);

    /// <summary>Fail-closed: lança a exceção correspondente se a operação global não for autorizada.</summary>
    ValueTask ExigirAsync(string permissaoGlobal, CancellationToken cancellationToken = default);
}
