using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Consultas;

namespace PortalBase.Application.Auditoria;

/// <summary>
/// Consulta da trilha de auditoria AUTORIZADA no servidor (seções 16, 17, 20, 24). Exige a
/// permissão <c>auditoria.visualizar</c> NO TENANT solicitado (validação server-side; nunca
/// <c>?tenantId=B</c> como bypass). Força o filtro de tenant ao tenant autorizado, pagina
/// obrigatoriamente e AUDITA o próprio acesso (evento de escrita <c>AuditQueried</c> — sem
/// recursão, pois auditar não dispara nova consulta). Depois de autorizado, mostra o NOME dos usuários atores (catálogo)
/// e aceita nome, login ou e-mail no filtro "Ator" — o identificador gravado continua sendo a referência da trilha.
/// </summary>
public sealed class ConsultaAuditoriaAutorizada
{
    /// <summary>Teto de identificadores que um texto do filtro "Ator" pode virar (mantém a consulta limitada).</summary>
    public const int MaximoAtoresPorFiltro = 50;

    private readonly IServicoAutorizacao _autorizacao;
    private readonly IConsultaAuditoria _consulta;
    private readonly IConsultaNomesAtores _nomes;
    private readonly IContextoSeguranca _seguranca;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public ConsultaAuditoriaAutorizada(
        IServicoAutorizacao autorizacao,
        IConsultaAuditoria consulta,
        IConsultaNomesAtores nomes,
        IContextoSeguranca seguranca,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(nomes);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _autorizacao = autorizacao;
        _consulta = consulta;
        _nomes = nomes;
        _seguranca = seguranca;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    public async Task<PaginaAuditoria> ConsultarAsync(Guid tenantId, FiltroAuditoria filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);

        // Fail-closed: sem a permissão no tenant informado, lança (auditado pelo próprio serviço).
        await _autorizacao.ExigirAsync(tenantId, Permissoes.AuditoriaVisualizar, cancellationToken).ConfigureAwait(false);

        // Período (Prompt 81): Desde > Até é rejeitado AQUI, no servidor — a UI só pré-valida.
        PeriodoConsulta.Validar(filtro.Desde, filtro.Ate);

        // O tenant do filtro é SEMPRE o autorizado — descarta qualquer valor vindo do cliente (inclusive a lista de atores,
        // que só o servidor resolve).
        var seguro = filtro.Normalizado() with { Tenant = tenantId.ToString(), Atores = null };
        if (!string.IsNullOrWhiteSpace(seguro.Ator))
        {
            // "Ator" aceita o identificador gravado OU o nome/login/e-mail de um usuário.
            var texto = seguro.Ator.Trim();
            var porNome = await _nomes.IdsPorTextoAsync(texto, MaximoAtoresPorFiltro, cancellationToken).ConfigureAwait(false);
            seguro = seguro with { Ator = texto, Atores = [texto, .. porNome.Take(MaximoAtoresPorFiltro)] };
        }

        var pagina = await IdentificarAtoresAsync(
            await _consulta.ConsultarAsync(seguro, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        // Auditar o acesso à auditoria (seção 24) — evento de escrita, não recorre em consulta.
        await _auditoria.RegistrarAsync(
            new EventoAuditoria(
                _seguranca.PrincipalId ?? "sistema",
                tenantId.ToString(),
                AcaoAuditoria.AuditQueried,
                ResultadoAuditoria.Permitido,
                _relogio.AgoraUtc)
            {
                TipoRecurso = "Auditoria",
                Metadados = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["pagina"] = seguro.Pagina.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["tamanho"] = seguro.TamanhoPagina.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
            cancellationToken).ConfigureAwait(false);

        return pagina;
    }

    // Nome e login de exibição dos usuários atores desta página (só os que vieram da consulta já autorizada).
    private async Task<PaginaAuditoria> IdentificarAtoresAsync(PaginaAuditoria pagina, CancellationToken cancellationToken)
    {
        var ids = pagina.Itens
            .Where(i => i.TipoAtor == TipoAtor.Usuario)
            .Select(i => i.PrincipalId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return pagina;
        }

        var nomes = await _nomes.IdentificarAsync(ids, cancellationToken).ConfigureAwait(false);
        if (nomes.Count == 0)
        {
            return pagina;
        }

        var itens = pagina.Itens
            .Select(i => i.TipoAtor == TipoAtor.Usuario && nomes.TryGetValue(i.PrincipalId, out var ator)
                ? i with { NomeAtor = ator.Nome, LoginAtor = ator.Login }
                : i)
            .ToList();
        return pagina with { Itens = itens };
    }
}
