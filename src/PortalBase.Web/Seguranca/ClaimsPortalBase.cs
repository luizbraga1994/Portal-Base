namespace PortalBase.Web.Seguranca;

/// <summary>Tipos de claim usados na sessão autenticada (fonte de verdade server-side).</summary>
public static class ClaimsPortalBase
{
    /// <summary>Marca de segurança da sessão; comparada com o usuário para revogar (seção 28).</summary>
    public const string SecurityStamp = "portalbase:security_stamp";

    /// <summary>Método de autenticação satisfeito (ex.: "mfa" após o step-up — seção 19).</summary>
    public const string Amr = "amr";

    public const string ValorMfa = "mfa";

    /// <summary>
    /// Dispensa do segundo fator: emitida pelo SERVIDOR no login de quem tem o perfil "Administrador da plataforma"
    /// (lido do catálogo). Vale como step-up satisfeito. Remover o perfil rotaciona o SecurityStamp, e a sessão que a
    /// carrega cai na revalidação do cookie.
    /// </summary>
    public const string DispensaMfa = "portalbase:dispensa_mfa";

    public const string ValorDispensaPlataforma = "administrador-plataforma";

    /// <summary>Step-up satisfeito nesta sessão: MFA feito ou dispensado pelo perfil "Administrador da plataforma".</summary>
    public static bool StepUpSatisfeito(System.Security.Claims.ClaimsPrincipal? principal)
        => principal is not null
            && (principal.HasClaim(Amr, ValorMfa) || principal.HasClaim(DispensaMfa, ValorDispensaPlataforma));

    /// <summary>
    /// Contribuinte (tenant) ATIVO selecionado nesta sessão (seções 13/14). Emitido pelo servidor no
    /// cookie (protegido por Data Protection — não forjável) somente APÓS validar o vínculo ativo do
    /// usuário. NUNCA autoriza sozinho: o acesso ao banco do tenant é sempre re-resolvido no servidor
    /// (regra 4/5/6). Serve para persistir a escolha entre requisições e recargas de página.
    /// </summary>
    public const string TenantSelecionado = "portalbase:tenant";

    /// <summary>Razão social do contribuinte selecionado — apenas para exibição (cosmético).</summary>
    public const string TenantNome = "portalbase:tenant_nome";

    /// <summary>
    /// PENDÊNCIA da conta que precisa ser resolvida antes de usar o portal, emitida pelo SERVIDOR no login a partir do
    /// cadastro do usuário: <see cref="PendenciaVerificacaoMfa"/> (MFA cadastrado e código ainda não informado),
    /// <see cref="PendenciaCadastroMfa"/> (MFA exigido e não cadastrado) ou <see cref="PendenciaTrocaSenha"/> (senha
    /// temporária). O <see cref="GuardaPendenciasConta"/> leva à tela certa.
    /// </summary>
    public const string Pendencia = "portalbase:pendencia";

    /// <summary>
    /// A senha foi aceita, mas o usuário tem MFA e ainda não informou o código: só a verificação do código (e sair) é
    /// servida — nem a seleção do contribuinte (SEC-001, auditoria de 08/10/2026). Some quando o código é validado.
    /// </summary>
    public const string PendenciaVerificacaoMfa = "verificacao-mfa";

    public const string PendenciaCadastroMfa = "cadastro-mfa";

    public const string PendenciaTrocaSenha = "troca-senha";
}
