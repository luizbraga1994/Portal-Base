using PortalBase.Application.Abstracoes;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Segredos;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Administracao;

/// <summary>
/// Read-models ADMINISTRATIVOS reais (Prompt 71) — projeções SOMENTE LEITURA e AUTORIZADAS sobre o
/// tenant corrente, construídas a partir dos repositórios já existentes (sem inventar dados). Nunca
/// expõem segredo, connection string, material criptográfico nem base física. A autorização é do
/// SERVIDOR (fail-closed): a consulta lança se não autorizada e o consumidor mapeia para o estado de UI.
/// </summary>

/// <summary>Detalhe (não secreto) do tenant corrente para a administração.</summary>
public sealed record DetalheTenant(
    Guid Id,
    string Nome,
    string Cnpj,
    string RazaoSocial,
    EstadoProvisionamento Estado,
    bool Operacional,
    string? SchemaVersion,
    string? UltimaMigracao,
    bool ComErro,
    DateTimeOffset CriadoEmUtc,
    DateTimeOffset AtualizadoEmUtc);

public interface IConsultaTenantAutorizada
{
    /// <summary>Detalhe do tenant CORRENTE (autorização admin.tenant). Null se não houver tenant no contexto.</summary>
    Task<DetalheTenant?> ObterAtualAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ConsultaTenantAutorizada : IConsultaTenantAutorizada
{
    private readonly IServicoAutorizacao _autorizacao;
    private readonly ITenantContext _contexto;
    private readonly IRepositorioTenant _repositorio;

    public ConsultaTenantAutorizada(IServicoAutorizacao autorizacao, ITenantContext contexto, IRepositorioTenant repositorio)
    {
        ArgumentNullException.ThrowIfNull(autorizacao);
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(repositorio);
        _autorizacao = autorizacao;
        _contexto = contexto;
        _repositorio = repositorio;
    }

    public async Task<DetalheTenant?> ObterAtualAsync(CancellationToken cancellationToken = default)
    {
        if (_contexto.TenantId is not Guid tenantId)
        {
            return null; // sem tenant selecionado → o consumidor mostra "Selecione um contribuinte".
        }

        // Administração de tenant: exige admin.tenant (fail-closed; lança se não autorizado).
        await _autorizacao.ExigirAdministrativoAsync(tenantId, Permissoes.AdminTenant, cancellationToken).ConfigureAwait(false);

        var tenant = await _repositorio.ObterPorIdAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return null;
        }

        return new DetalheTenant(
            tenant.Id,
            tenant.Nome,
            tenant.Cnpj.ToString(),
            tenant.RazaoSocial,
            tenant.Estado,
            tenant.EstaOperacional,
            tenant.SchemaVersion,
            tenant.UltimaMigracao,
            !string.IsNullOrWhiteSpace(tenant.UltimoErro), // apenas SINALIZA erro; nunca expõe o texto interno.
            tenant.CriadoEmUtc,
            tenant.AtualizadoEmUtc);
    }
}
