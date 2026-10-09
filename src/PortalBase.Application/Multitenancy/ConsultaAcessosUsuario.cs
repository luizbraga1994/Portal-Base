namespace PortalBase.Application.Multitenancy;

/// <summary>
/// Contribuinte (tenant) ao qual o usuário possui vínculo ATIVO — projeção somente-leitura para a
/// etapa de SELEÇÃO de contribuinte após o login. Não carrega dado do tenant nem credencial: apenas a
/// identidade mínima para o usuário escolher entre os contribuintes que já lhe pertencem.
/// </summary>
/// <param name="Nome">Nome escolhido do tenant (MAIÚSCULO) — o que a seleção de contribuinte mostra.</param>
public sealed record AcessoContribuinte(Guid TenantId, string Nome, string RazaoSocial, string Cnpj, bool Operacional);

/// <summary>
/// Lista os contribuintes aos quais o usuário AUTENTICADO possui vínculo ATIVO (seções 5/10). É o
/// insumo do passo de seleção pós-login: um usuário pode acessar VÁRIOS contribuintes, mas só os que
/// já lhe foram concedidos. O tenant nunca vem do cliente — esta consulta parte SEMPRE do usuário
/// autenticado (regra 4). Deny-by-default: sem vínculo ativo, a lista é vazia.
/// </summary>
public interface IConsultaAcessosUsuario
{
    Task<IReadOnlyList<AcessoContribuinte>> ListarDoUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}
