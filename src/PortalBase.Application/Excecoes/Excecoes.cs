namespace PortalBase.Application.Excecoes;

/// <summary>
/// Base para exceções de aplicação/segurança. Mensagens seguras, sem detalhes
/// internos (regra 16).
/// </summary>
public abstract class AplicacaoException : Exception
{
    protected AplicacaoException()
    {
    }

    protected AplicacaoException(string message)
        : base(message)
    {
    }

    protected AplicacaoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Requisição sem principal autenticado (regra 3).</summary>
public sealed class NaoAutenticadoException : AplicacaoException
{
    public NaoAutenticadoException()
        : base("Requisição não autenticada.")
    {
    }

    public NaoAutenticadoException(string message)
        : base(message)
    {
    }

    public NaoAutenticadoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Tenant inexistente no catálogo. Resposta genérica (deny-by-default; não revela estado).</summary>
public sealed class TenantNaoEncontradoException : AplicacaoException
{
    public TenantNaoEncontradoException()
        : base("Tenant não encontrado ou indisponível.")
    {
    }

    public TenantNaoEncontradoException(string message)
        : base(message)
    {
    }

    public TenantNaoEncontradoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Principal autenticado não autorizado ao tenant solicitado (regras 3, 4).</summary>
public sealed class TenantNaoAutorizadoException : AplicacaoException
{
    public TenantNaoAutorizadoException()
        : base("Acesso ao tenant não autorizado.")
    {
    }

    public TenantNaoAutorizadoException(string message)
        : base(message)
    {
    }

    public TenantNaoAutorizadoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Operação de dado do tenant tentada sem um <c>TenantContext</c> válido e autorizado (regra 5).</summary>
public sealed class TenantContextAusenteException : AplicacaoException
{
    public TenantContextAusenteException()
        : base("Operação de dado do tenant requer um TenantContext válido e autorizado.")
    {
    }

    public TenantContextAusenteException(string message)
        : base(message)
    {
    }

    public TenantContextAusenteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Violação de uma restrição de unicidade do banco (segunda barreira contra
/// duplicidade sob concorrência — seção 25). Lançada pela infraestrutura e traduzida
/// pelo caso de uso em um resultado de conflito.
/// </summary>
public sealed class ViolacaoUnicidadeException : AplicacaoException
{
    public ViolacaoUnicidadeException()
        : base("Violação de restrição de unicidade.")
    {
    }

    public ViolacaoUnicidadeException(string message)
        : base(message)
    {
    }

    public ViolacaoUnicidadeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Nenhuma conexão registrada para o tenant. NÃO existe fallback para conexão
/// padrão de contribuinte (regra 6): a ausência de registro é um erro.
/// </summary>
public sealed class ConexaoTenantNaoEncontradaException : AplicacaoException
{
    public ConexaoTenantNaoEncontradaException()
        : base("Nenhuma conexão registrada para o tenant.")
    {
    }

    public ConexaoTenantNaoEncontradaException(string message)
        : base(message)
    {
    }

    public ConexaoTenantNaoEncontradaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Credenciais de autenticação inválidas. Mensagem GENÉRICA e idêntica para usuário
/// inexistente, senha incorreta ou conta bloqueada (anti-enumeração — seções 7, 26).
/// </summary>
public sealed class CredenciaisInvalidasException : AplicacaoException
{
    public CredenciaisInvalidasException()
        : base("Credenciais inválidas.")
    {
    }

    public CredenciaisInvalidasException(string message)
        : base(message)
    {
    }

    public CredenciaisInvalidasException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Operação negada por falta de papel/permissão no tenant (autorização server-side —
/// seções 12, 16, 22). Deny-by-default: ausência de policy ⇒ negado.
/// </summary>
public sealed class AutorizacaoNegadaException : AplicacaoException
{
    public AutorizacaoNegadaException()
        : base("Operação não autorizada.")
    {
    }

    public AutorizacaoNegadaException(string message)
        : base(message)
    {
    }

    public AutorizacaoNegadaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Operação de alto impacto exige segundo fator satisfeito nesta sessão e ele não está
/// (step-up — seções 18, 19, 20). Nunca considerar apenas o papel como suficiente.
/// </summary>
public sealed class MfaRequeridoException : AplicacaoException
{
    public MfaRequeridoException()
        : base("Segundo fator (MFA) requerido para esta operação.")
    {
    }

    public MfaRequeridoException(string message)
        : base(message)
    {
    }

    public MfaRequeridoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Código/segredo de MFA inválido ou expirado (seções 18, 36). Nunca ecoa o código.</summary>
public sealed class MfaInvalidoException : AplicacaoException
{
    public MfaInvalidoException()
        : base("Código de verificação inválido.")
    {
    }

    public MfaInvalidoException(string message)
        : base(message)
    {
    }

    public MfaInvalidoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Tentativa de auto-concessão de privilégio (seção 21): o próprio ator não pode conceder
/// a si mesmo acesso/papel/permissão privilegiada. Bloqueado incondicionalmente.
/// </summary>
public sealed class AutoConcessaoNaoPermitidaException : AplicacaoException
{
    public AutoConcessaoNaoPermitidaException()
        : base("Um usuário não pode conceder privilégios a si mesmo.")
    {
    }

    public AutoConcessaoNaoPermitidaException(string message)
        : base(message)
    {
    }

    public AutoConcessaoNaoPermitidaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

