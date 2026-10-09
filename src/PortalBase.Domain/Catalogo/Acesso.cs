namespace PortalBase.Domain.Catalogo;

/// <summary>
/// Vínculo explícito Usuário → Tenant. Um usuário pode acessar vários contribuintes,
/// mas SOMENTE os tenants aos quais possui vínculo válido (o tenant nunca é confiável
/// vindo de URL/query/formulário — seção 10). Sem duplicidade (usuário, tenant).
/// </summary>
public sealed class UsuarioTenant
{
    private UsuarioTenant()
    {
    }

    private UsuarioTenant(Guid id, Guid usuarioId, Guid tenantId, DateTimeOffset agoraUtc)
    {
        Id = id;
        UsuarioId = usuarioId;
        TenantId = tenantId;
        Status = StatusAcesso.Ativo;
        CriadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public Guid Id { get; private set; }

    public Guid UsuarioId { get; private set; }

    public Guid TenantId { get; private set; }

    public StatusAcesso Status { get; private set; }

    public DateTimeOffset CriadoEmUtc { get; private set; }

    public DateTimeOffset? RevogadoEmUtc { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public bool EstaAtivo => Status == StatusAcesso.Ativo;

    public static UsuarioTenant Conceder(Guid usuarioId, Guid tenantId, DateTimeOffset agoraUtc, Guid? id = null)
    {
        if (usuarioId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Usuário e tenant são obrigatórios para conceder acesso.");
        }

        return new UsuarioTenant(id ?? Guid.NewGuid(), usuarioId, tenantId, agoraUtc);
    }

    /// <summary>Devolve o acesso a um vínculo revogado (o histórico de criação é mantido).</summary>
    public void Reativar()
    {
        if (Status == StatusAcesso.Ativo)
        {
            return;
        }

        Status = StatusAcesso.Ativo;
        RevogadoEmUtc = null;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public void Revogar(DateTimeOffset agoraUtc)
    {
        if (Status == StatusAcesso.Revogado)
        {
            return;
        }

        Status = StatusAcesso.Revogado;
        RevogadoEmUtc = agoraUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }
}

/// <summary>
/// Papel do usuário ESCOPADO a um vínculo Usuário×Tenant (seção 11): o papel não vale
/// para todos os tenants sem autorização explícita. Chave composta; sem duplicidade.
/// </summary>
public sealed class UsuarioTenantPapel
{
    private UsuarioTenantPapel()
    {
    }

    public UsuarioTenantPapel(Guid usuarioTenantId, Guid papelId)
    {
        UsuarioTenantId = usuarioTenantId;
        PapelId = papelId;
    }

    public Guid UsuarioTenantId { get; private set; }

    public Guid PapelId { get; private set; }
}
