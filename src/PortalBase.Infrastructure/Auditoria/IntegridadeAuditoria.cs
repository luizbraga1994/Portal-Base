using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Infrastructure.Opcoes;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Auditoria;

/// <summary>
/// Evidência de integridade (seção 14): calcula um HMAC-SHA256 sobre os campos canônicos de um
/// registro, com chave no cofre (nunca no código). Adulterar qualquer campo faz o HMAC divergir
/// — sem a chave, não é possível recomputá-lo. Fornece <b>tamper-evidence em nível de
/// aplicação/banco</b> (detecção de modificação); NÃO é, por si só, garantia jurídica de
/// imutabilidade absoluta. A não-remoção é reforçada por append-only + grants (sem UPDATE/DELETE).
/// </summary>
public sealed class IntegridadeAuditoria
{
    private readonly IProvedorSegredos _segredos;
    private readonly OpcoesAuditoria _opcoes;

    public IntegridadeAuditoria(IProvedorSegredos segredos, IOptions<OpcoesAuditoria> opcoes)
    {
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentNullException.ThrowIfNull(opcoes);
        _segredos = segredos;
        _opcoes = opcoes.Value;
    }

    /// <summary>Calcula o hash de integridade de um registro (hex maiúsculo).</summary>
    public async ValueTask<string> CalcularAsync(RegistroAuditoria registro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registro);
        var chave = await ObterChaveAsync(cancellationToken).ConfigureAwait(false);
        var canonico = Canonizar(registro);
        var hash = HMACSHA256.HashData(chave, Encoding.UTF8.GetBytes(canonico));
        return Convert.ToHexString(hash);
    }

    /// <summary>Verifica se o hash armazenado corresponde ao conteúdo atual (comparação constante).</summary>
    public async ValueTask<bool> VerificarAsync(RegistroAuditoria registro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registro);
        var esperado = await CalcularAsync(registro, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(esperado),
            Encoding.ASCII.GetBytes(registro.IntegridadeHash ?? string.Empty));
    }

    // Serialização canônica e determinística dos campos auditáveis (a ordem é fixa).
    private static string Canonizar(RegistroAuditoria r)
    {
        var construtor = new StringBuilder(512);
        void Campo(string? valor) => construtor.Append(valor ?? string.Empty).Append('\u001e');

        Campo(r.Tenant);
        Campo(r.PrincipalId);
        Campo(r.TipoAtor.ToString(CultureInfo.InvariantCulture));
        Campo(r.Operacao);
        Campo(r.TipoRecurso);
        Campo(r.RecursoId);
        Campo(r.Resultado);
        Campo(r.Motivo);
        Campo(r.CorrelationId);
        Campo(r.TraceId);
        Campo(r.IpAddress);
        Campo(r.UserAgent);
        Campo(r.Origem);
        Campo(r.Job);
        Campo(r.Competencia);
        Campo(r.Protocolo);
        Campo(r.Categoria.ToString(CultureInfo.InvariantCulture));
        Campo(r.ValoresAnteriores);
        Campo(r.ValoresNovos);
        Campo(r.Metadados);
        // Timestamp canônico ESTÁVEL no roundtrip do banco: independente de Kind e de precisão
        // da coluna (segundos). Evita divergência entre o valor em memória e o relido do MySQL.
        var instante = new DateTimeOffset(DateTime.SpecifyKind(r.OcorridoEmUtc, DateTimeKind.Utc));
        Campo(instante.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return construtor.ToString();
    }

    private async ValueTask<byte[]> ObterChaveAsync(CancellationToken cancellationToken)
    {
        var referencia = _opcoes.ChaveIntegridadeRef;
        if (string.IsNullOrWhiteSpace(referencia))
        {
            throw new InvalidOperationException("Auditoria:ChaveIntegridadeRef não configurada.");
        }

        var base64 = await _segredos.ObterAsync(referencia, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(base64);
    }
}
