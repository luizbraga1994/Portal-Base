using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Multitenancy;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Application.Administracao;

/// <summary>Dados cadastrais EDITÁVEIS do contribuinte, como vieram da tela (validados no domínio).</summary>
public sealed record DadosCadastroTenant(string? RazaoSocial);

/// <summary>Desfecho da edição: <see cref="Alterado"/> = algo mudou (sem mudança, nada é gravado nem auditado).</summary>
public sealed record ResultadoEdicaoCadastroTenant(bool Sucesso, bool Alterado, string? Erro)
{
    public static readonly ResultadoEdicaoCadastroTenant SemMudanca = new(true, false, null);

    public static readonly ResultadoEdicaoCadastroTenant Gravado = new(true, true, null);

    public static ResultadoEdicaoCadastroTenant Recusado(string erro) => new(false, false, erro);
}

/// <summary>
/// Edição dos dados cadastrais do contribuinte SELECIONADO (razão social — acrescente os campos do seu domínio) pela
/// tela Administração → Tenant. CNPJ e nome do tenant não mudam (identificam o contribuinte e a base).
/// Fail-closed: tenant do SERVIDOR (nunca do cliente); exige <see cref="Permissoes.AdminTenant"/> (Administrador do
/// contribuinte ou da plataforma); concorrência otimista pela data da última atualização que a tela viu; auditoria com
/// antes/depois só dos campos alterados (dados cadastrais públicos — nunca segredo).
/// </summary>
public sealed class EdicaoCadastroTenant
{
    private readonly ITenantContext _tenant;
    private readonly IServicoAutorizacao _autorizacao;
    private readonly IContextoSeguranca _seguranca;
    private readonly IRepositorioTenant _repositorio;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditoria _auditoria;
    private readonly IRelogio _relogio;

    public EdicaoCadastroTenant(
        ITenantContext tenant,
        IServicoAutorizacao autorizacao,
        IContextoSeguranca seguranca,
        IRepositorioTenant repositorio,
        IUnitOfWork unitOfWork,
        IAuditoria auditoria,
        IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(seguranca);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(auditoria);
        ArgumentNullException.ThrowIfNull(relogio);

        _tenant = tenant;
        _autorizacao = autorizacao;
        _seguranca = seguranca;
        _repositorio = repositorio;
        _unitOfWork = unitOfWork;
        _auditoria = auditoria;
        _relogio = relogio;
    }

    /// <param name="dados">Os campos editáveis.</param>
    /// <param name="atualizadoEmVisto">A "Atualizado em" que a tela mostrava: se mudou desde então, a edição é recusada.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <exception cref="TenantContextAusenteException">Sem contribuinte autorizado.</exception>
    /// <exception cref="AutorizacaoNegadaException">Sem admin.tenant.</exception>
    public async Task<ResultadoEdicaoCadastroTenant> AtualizarAsync(
        DadosCadastroTenant dados, DateTimeOffset? atualizadoEmVisto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dados);
        if (!_tenant.EstaAutorizado || _tenant.TenantId is not Guid tenantId)
        {
            throw new TenantContextAusenteException();
        }

        await _autorizacao.ExigirAdministrativoAsync(tenantId, Permissoes.AdminTenant, cancellationToken).ConfigureAwait(false);

        var tenant = await _repositorio.ObterPorIdAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantNaoEncontradoException();

        if (atualizadoEmVisto is { } visto && tenant.AtualizadoEmUtc != visto)
        {
            return ResultadoEdicaoCadastroTenant.Recusado(
                "O cadastro foi alterado por outra pessoa enquanto você editava. Recarregue a página e faça a alteração de novo.");
        }

        var antes = Campos(tenant.RazaoSocial);
        bool mudou;
        try
        {
            mudou = tenant.AtualizarCadastro(dados.RazaoSocial, _relogio.AgoraUtc);
        }
        catch (EntradaInvalidaException)
        {
            return ResultadoEdicaoCadastroTenant.Recusado(
                $"Preencha a razão social (até {Domain.Catalogo.Tenant.TamanhoMaximoRazaoSocial} caracteres).");
        }

        if (!mudou)
        {
            return ResultadoEdicaoCadastroTenant.SemMudanca;
        }

        await _unitOfWork.SalvarAlteracoesAsync(cancellationToken).ConfigureAwait(false);

        var depois = Campos(tenant.RazaoSocial);
        var alterados = antes.Keys.Where(k => !string.Equals(antes[k], depois[k], StringComparison.Ordinal)).ToList();
        await _auditoria.RegistrarAsync(
            new EventoAuditoria(_seguranca.PrincipalId ?? "sistema", tenantId.ToString(), AcaoAuditoria.TenantDetailsUpdated, ResultadoAuditoria.Permitido, _relogio.AgoraUtc)
            {
                TipoRecurso = "Tenant",
                RecursoId = tenantId.ToString(),
                ValoresAnteriores = alterados.ToDictionary(k => k, k => antes[k]),
                ValoresNovos = alterados.ToDictionary(k => k, k => depois[k]),
            },
            cancellationToken).ConfigureAwait(false);

        return ResultadoEdicaoCadastroTenant.Gravado;
    }

    private static Dictionary<string, string?> Campos(string razao) => new(StringComparer.Ordinal)
    {
        ["razaoSocial"] = razao,
    };
}
