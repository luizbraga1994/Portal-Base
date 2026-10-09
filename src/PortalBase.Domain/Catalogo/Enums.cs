namespace PortalBase.Domain.Catalogo;

/// <summary>Situação de um usuário no catálogo.</summary>
public enum StatusUsuario
{
    Ativo = 1,
    Inativo = 2,
    Bloqueado = 3,
}

/// <summary>Situação do vínculo de acesso de um usuário a um tenant.</summary>
public enum StatusAcesso
{
    Ativo = 1,
    Revogado = 2,
}

/// <summary>Papéis básicos centralizados (conforme documentação).</summary>
public enum PapelConhecido
{
    Leitor = 1,
    Analista = 2,
    Administrador = 3,
}

/// <summary>
/// Permissões granulares conhecidas (de TENANT). A arquitetura suporta novas permissões sem alteração estrutural:
/// acrescente o valor AO FINAL (os GUIDs do seed derivam do valor), mapeie a chave em
/// <c>CatalogoPapeisPermissoes.ChaveDe</c>, conceda na matriz e gere a migration do catálogo.
/// </summary>
public enum PermissaoConhecida
{
    VisualizarDados = 1,
    Cadastrar = 2,
    Editar = 3,
    Excluir = 4,
    Importar = 5,
    AdministrarTenant = 6,
    AdministrarUsuarios = 7,
    VisualizarAuditoria = 8,
}
