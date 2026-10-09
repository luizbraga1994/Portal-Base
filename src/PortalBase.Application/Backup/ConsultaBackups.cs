using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Domain.Backup;

namespace PortalBase.Application.Backup;

/// <summary>
/// Backup recente para a tela de Operações — só metadados: tipo, contribuinte (Id), situação, datas, tamanho, cifra,
/// retenção, teste de restauração e o TIPO do erro. Nunca local físico, nome de base, chave ou connection string.
/// </summary>
public sealed record ItemBackup(
    Guid Id,
    TipoBackup Tipo,
    Guid? TenantId,
    string? NomeContribuinte,
    StatusBackup Status,
    DateTimeOffset IniciadoEmUtc,
    DateTimeOffset? ConcluidoEmUtc,
    long TamanhoBytes,
    bool Criptografado,
    DateTimeOffset? RetencaoAteUtc,
    bool RestoreTestado,
    string? Erro,
    SituacaoCopiaExterna? CopiaExterna = null,
    string? HashSha256 = null);

/// <summary>Consulta dos backups recentes — autorização de PLATAFORMA (a mesma de executar backup), no servidor.</summary>
public sealed class ConsultaBackupsAutorizada
{
    public const int Maximo = 100;
    private const int MaximoContribuintes = 5000;

    private readonly IAutorizacaoPlataforma _autorizacao;
    private readonly IRepositorioRegistroBackup _registros;
    private readonly IConsultaContribuintesPlataforma _contribuintes;

    public ConsultaBackupsAutorizada(IAutorizacaoPlataforma autorizacao, IRepositorioRegistroBackup registros, IConsultaContribuintesPlataforma contribuintes)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(registros);
        ArgumentNullException.ThrowIfNull(contribuintes);
        _autorizacao = autorizacao;
        _registros = registros;
        _contribuintes = contribuintes;
    }

    public async Task<IReadOnlyList<ItemBackup>> ListarRecentesAsync(int quantidade = 50, CancellationToken cancellationToken = default)
    {
        await _autorizacao.ExigirAsync(Permissoes.BackupExecutar, cancellationToken).ConfigureAwait(false);
        var registros = await _registros.ListarRecentesAsync(Math.Clamp(quantidade, 1, Maximo), cancellationToken).ConfigureAwait(false);
        if (registros.Count == 0)
        {
            return [];
        }

        var nomes = registros.Any(r => r.TenantId is not null)
            ? (await _contribuintes.ListarAsync(MaximoContribuintes, cancellationToken).ConfigureAwait(false)).ToDictionary(c => c.Id, c => c.Nome)
            : [];
        return registros
            .Select(r => new ItemBackup(r.Id, r.Tipo, r.TenantId, r.TenantId is { } t ? nomes.GetValueOrDefault(t) : null, r.Status,
                r.IniciadoEmUtc, r.ConcluidoEmUtc, r.TamanhoBytes, r.Criptografado, r.RetencaoAteUtc, r.RestoreTestado, r.UltimoErro,
                r.CopiaExterna, r.HashSha256))
            .ToList();
    }
}
