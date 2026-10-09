using PortalBase.Domain.Comum;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Catalogo;

/// <summary>
/// Papel (perfil) do catálogo. Os papéis do SISTEMA (Leitor, Analista, Administrador) valem para todos
/// os contribuintes e não mudam pela tela (<see cref="TenantId"/> nulo). Os PERSONALIZADOS pertencem a UM contribuinte
/// (<see cref="TenantId"/>) e são criados/alterados/excluídos pelo administrador dele — nunca valem em outro.
/// </summary>
public sealed class Papel
{
    public const int LimiteNome = 64;
    public const int LimiteDescricao = 256;

    private Papel()
    {
    }

    private Papel(Guid id, string nome, string descricao, Guid? tenantId)
    {
        Id = id;
        Nome = nome;
        Descricao = descricao;
        TenantId = tenantId;
    }

    public Guid Id { get; private set; }

    public string Nome { get; private set; } = null!;

    public string Descricao { get; private set; } = null!;

    /// <summary>Contribuinte dono do perfil personalizado; <c>null</c> = papel do sistema.</summary>
    public Guid? TenantId { get; private set; }

    public bool EhDoSistema => TenantId is null;

    public static Papel Criar(Guid id, string nome, string descricao)
        => new(
            id,
            Exigir.TextoObrigatorioComLimite(nome, LimiteNome, nameof(Nome)),
            Exigir.TextoObrigatorioComLimite(descricao, LimiteDescricao, nameof(Descricao)),
            tenantId: null);

    /// <summary>Perfil personalizado de um contribuinte.</summary>
    public static Papel CriarPersonalizado(Guid tenantId, string nome, string? descricao, Guid? id = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new EntradaInvalidaException("O contribuinte do perfil é obrigatório.");
        }

        return new Papel(
            id ?? Guid.NewGuid(),
            Exigir.TextoObrigatorioComLimite(nome?.Trim(), LimiteNome, nameof(Nome)),
            DescricaoOuPadrao(descricao),
            tenantId);
    }

    /// <summary>Renomeia/redescreve um perfil PERSONALIZADO (os do sistema não mudam).</summary>
    public void Alterar(string nome, string? descricao)
    {
        if (EhDoSistema)
        {
            throw new EstadoInvalidoException("Os perfis do sistema não podem ser alterados.");
        }

        Nome = Exigir.TextoObrigatorioComLimite(nome?.Trim(), LimiteNome, nameof(Nome));
        Descricao = DescricaoOuPadrao(descricao);
    }

    private static string DescricaoOuPadrao(string? descricao)
        => string.IsNullOrWhiteSpace(descricao)
            ? "Perfil personalizado."
            : Exigir.TextoObrigatorioComLimite(descricao.Trim(), LimiteDescricao, nameof(Descricao));
}

/// <summary>Permissão granular centralizada no catálogo (chave estável + descrição).</summary>
public sealed class Permissao
{
    private const int LimiteChave = 64;
    private const int LimiteDescricao = 256;

    private Permissao()
    {
    }

    private Permissao(Guid id, string chave, string descricao)
    {
        Id = id;
        Chave = chave;
        Descricao = descricao;
    }

    public Guid Id { get; private set; }

    /// <summary>Chave estável usada pela autorização (ex.: "dados.visualizar").</summary>
    public string Chave { get; private set; } = null!;

    public string Descricao { get; private set; } = null!;

    public static Permissao Criar(Guid id, string chave, string descricao)
        => new(
            id,
            Exigir.TextoObrigatorioComLimite(chave, LimiteChave, nameof(Chave)),
            Exigir.TextoObrigatorioComLimite(descricao, LimiteDescricao, nameof(Descricao)));
}

/// <summary>Vínculo Papel → Permissão (chave composta; sem duplicidade).</summary>
public sealed class PapelPermissao
{
    private PapelPermissao()
    {
    }

    public PapelPermissao(Guid papelId, Guid permissaoId)
    {
        PapelId = papelId;
        PermissaoId = permissaoId;
    }

    public Guid PapelId { get; private set; }

    public Guid PermissaoId { get; private set; }
}
