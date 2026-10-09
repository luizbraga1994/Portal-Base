using PortalBase.Application.Autorizacao;
using PortalBase.Application.Consultas;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Administracao;

// =====================================================================================================
// Read-model ADMINISTRATIVO de USUÁRIOS por tenant (Prompt 72). Projeção SOMENTE LEITURA e AUTORIZADA
// dos usuários VINCULADOS ao tenant corrente, sobre o catálogo central (Usuario × UsuarioTenant × Papel).
// O tenant é resolvido no SERVIDOR (nunca do cliente) e o escopo é aplicado na consulta (WHERE TenantId),
// pois o catálogo é COMPARTILHADO (não é banco por tenant). NUNCA expõe hash de senha, segredo de MFA,
// security stamp, concurrency stamp nem dados de outro tenant. Autorização fail-closed (lança se negada).
// =====================================================================================================

/// <summary>Campos suportados de ordenação (whitelist — nunca campo arbitrário vindo do cliente).</summary>
public enum CampoOrdenacaoUsuario
{
    Login,
    Nome,
    Status,
    CriadoEm,
    UltimoAcesso,
}

/// <summary>Projeção ADMINISTRATIVA (não secreta) de um usuário vinculado ao tenant.</summary>
public sealed record UsuarioAdministracaoDto(
    Guid UsuarioId,
    string Login,
    string Nome,
    string Email,
    StatusUsuario Status,
    StatusAcesso StatusAcesso,
    bool MfaHabilitado,
    DateTimeOffset? BloqueioTemporarioAteUtc,
    IReadOnlyList<string> Papeis,
    DateTimeOffset CriadoEmUtc,
    DateTimeOffset? UltimoAcessoUtc,
    DateTimeOffset VinculoConcedidoEmUtc,
    bool AdministradorPlataforma = false,
    bool TemOutrosContribuintes = false,
    IReadOnlyList<Guid>? PerfilIds = null,
    bool PossuiSenha = true,
    bool MfaObrigatorio = false,
    bool SenhaTemporaria = false);

/// <summary>Filtro da listagem administrativa de usuários (normalizado no servidor — anti-abuso).</summary>
public sealed record FiltroUsuariosAdmin
{
    private const int TamanhoPaginaPadrao = 25;
    private const int TamanhoPaginaMaximo = 100;
    private const int LimiteBusca = 128;

    /// <summary>Busca textual (login/nome/email). Normalizada; tamanho limitado.</summary>
    public string? Busca { get; init; }

    /// <summary>Status GLOBAL do usuário; null = todos.</summary>
    public StatusUsuario? Status { get; init; }

    /// <summary>Status do VÍNCULO no tenant (Ativo/Revogado); null = todos.</summary>
    public StatusAcesso? StatusAcesso { get; init; }

    public CampoOrdenacaoUsuario Ordenacao { get; init; } = CampoOrdenacaoUsuario.Login;

    public bool Descendente { get; init; }

    public int Pagina { get; init; } = 1;

    public int TamanhoPagina { get; init; } = TamanhoPaginaPadrao;

    /// <summary>Normaliza limites e busca (evita paginação abusiva e wildcard sem tamanho — §5/§6).</summary>
    public FiltroUsuariosAdmin Normalizado() => this with
    {
        Busca = string.IsNullOrWhiteSpace(Busca) ? null
            : Busca.Trim() is { Length: > LimiteBusca } b ? b[..LimiteBusca] : Busca.Trim(),
        Pagina = Pagina < 1 ? 1 : Pagina,
        TamanhoPagina = TamanhoPagina is < 1 or > TamanhoPaginaMaximo ? TamanhoPaginaPadrao : TamanhoPagina,
    };
}

/// <summary>
/// Porta de CONSULTA (implementada na Infraestrutura sobre o catálogo). Recebe o tenant JÁ RESOLVIDO
/// e AUTORIZADO pelo servidor — nunca resolve autorização por conta própria. Projeta somente campos
/// seguros; ordena de forma estável (desempate por Id); pagina no banco (nunca em memória).
/// </summary>
public interface IConsultaUsuariosAdmin
{
    Task<ResultadoPaginado<UsuarioAdministracaoDto>> ListarPorTenantAsync(Guid tenantId, FiltroUsuariosAdmin filtro, CancellationToken cancellationToken = default);

    /// <summary>Consulta DIRETA por identificador, escopada ao tenant (IDENTIDADE → QUERY → RESULTADO ÚNICO).</summary>
    Task<UsuarioAdministracaoDto?> ObterDoTenantAsync(Guid tenantId, Guid usuarioId, CancellationToken cancellationToken = default);
}

/// <summary>Consulta AUTORIZADA de usuários administrativos (Prompt 72): tenant do contexto + admin.usuarios.</summary>
public interface IConsultaUsuariosAutorizada
{
    /// <summary>Lista os usuários do tenant CORRENTE. Sem tenant → página vazia (fail-closed).</summary>
    Task<ResultadoPaginado<UsuarioAdministracaoDto>> ListarAsync(FiltroUsuariosAdmin filtro, CancellationToken cancellationToken = default);

    /// <summary>Detalhe de um usuário do tenant CORRENTE por Id. Null se não houver tenant ou vínculo.</summary>
    Task<UsuarioAdministracaoDto?> ObterAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ConsultaUsuariosAutorizada : IConsultaUsuariosAutorizada
{
    private readonly ITenantContext _contexto;
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IConsultaUsuariosAdmin _consulta;

    public ConsultaUsuariosAutorizada(ITenantContext contexto, IServicoAutorizacao autorizacao, IConsultaUsuariosAdmin consulta)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(consulta);
        _contexto = contexto;
        _autorizacao = autorizacao;
        _consulta = consulta;
    }

    public async Task<ResultadoPaginado<UsuarioAdministracaoDto>> ListarAsync(FiltroUsuariosAdmin filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var normalizado = filtro.Normalizado();

        if (_contexto.TenantId is not Guid tenantId)
        {
            return ResultadoPaginado.Vazio<UsuarioAdministracaoDto>(normalizado.Pagina, normalizado.TamanhoPagina);
        }

        // Administração de usuários: exige admin.usuarios (administrativo; fail-closed — lança se negado).
        await _autorizacao.ExigirAdministrativoAsync(tenantId, Permissoes.AdminUsuarios, cancellationToken).ConfigureAwait(false);
        return await _consulta.ListarPorTenantAsync(tenantId, normalizado, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsuarioAdministracaoDto?> ObterAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (_contexto.TenantId is not Guid tenantId)
        {
            return null;
        }

        await _autorizacao.ExigirAdministrativoAsync(tenantId, Permissoes.AdminUsuarios, cancellationToken).ConfigureAwait(false);
        return await _consulta.ObterDoTenantAsync(tenantId, usuarioId, cancellationToken).ConfigureAwait(false);
    }
}
