using System.Text.Json;
using PortalBase.Application.Auditoria;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Auditoria;

/// <summary>
/// Trilha de auditoria sobre o CATÁLOGO (regra 9; Prompt 05). Enriquece o evento com o
/// contexto de correlação ambiente (correlation/trace/IP/user-agent/origem/ator), SANITIZA
/// valores antes/depois e metadados (nunca segredos — seções 6, 7, 13), calcula o hash de
/// integridade e INSERE (append-only, sem UPDATE/DELETE — seção 14).
///
/// Estratégia transacional (seção 9): o registro é auto-committado (append durável). Como
/// compartilha o mesmo <c>DbContext</c> (escopo), nas mutações críticas ele é gravado logo
/// após o commit de negócio, na mesma requisição. Persistir na mesma transação de negócio ou
/// via outbox é endurecimento futuro.
/// </summary>
public sealed class AuditoriaEfCore : IAuditoria
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly PortalBaseCatalogoDbContext _contexto;
    private readonly IContextoAuditoria _correlacao;
    private readonly IntegridadeAuditoria _integridade;

    public AuditoriaEfCore(
        PortalBaseCatalogoDbContext contexto,
        IContextoAuditoria correlacao,
        IntegridadeAuditoria integridade)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(correlacao);
        ArgumentNullException.ThrowIfNull(integridade);
        _contexto = contexto;
        _correlacao = correlacao;
        _integridade = integridade;
    }

    public async ValueTask RegistrarAsync(EventoAuditoria evento, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evento);

        var registro = new RegistroAuditoria
        {
            Tenant = evento.Tenant,
            PrincipalId = evento.PrincipalId,
            TipoAtor = (int)_correlacao.TipoAtor,
            Operacao = evento.Operacao,
            TipoRecurso = evento.TipoRecurso,
            RecursoId = evento.RecursoId,
            Resultado = evento.Resultado.ToString(),
            Motivo = SanitizacaoAuditoria.SanitizarTexto(evento.Motivo),
            CorrelationId = _correlacao.CorrelationId,
            TraceId = _correlacao.TraceId,
            IpAddress = _correlacao.IpAddress,
            UserAgent = _correlacao.UserAgent,
            Origem = _correlacao.Origem,
            Job = _correlacao.Job,
            Competencia = evento.Competencia,
            Protocolo = evento.Protocolo,
            Categoria = (int)evento.Categoria,
            ValoresAnteriores = SerializarSanitizado(evento.ValoresAnteriores),
            ValoresNovos = SerializarSanitizado(evento.ValoresNovos),
            Metadados = SerializarSanitizado(evento.Metadados),
            OcorridoEmUtc = evento.OcorridoEm.UtcDateTime,
            IntegridadeHash = string.Empty,
        };

        registro.IntegridadeHash = await _integridade.CalcularAsync(registro, cancellationToken).ConfigureAwait(false);

        _contexto.RegistrosAuditoria.Add(registro);
        await _contexto.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? SerializarSanitizado(IReadOnlyDictionary<string, string?>? valores)
    {
        if (valores is null || valores.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(SanitizacaoAuditoria.Sanitizar(valores), Json);
    }
}
