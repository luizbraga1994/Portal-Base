namespace PortalBase.Application.Autorizacao;

/// <summary>
/// Contexto de segurança com escopo de requisição. DENY-BY-DEFAULT: começa anônimo e sem
/// MFA. É preenchido pela apresentação/host a partir da sessão autenticada validada no
/// servidor (nunca do estado de componente Blazor — seções 9, 42). A camada Application
/// consome apenas a face de leitura (<see cref="IContextoSeguranca"/>).
/// </summary>
public sealed class ContextoSeguranca : IContextoSegurancaMutavel
{
    public bool EstaAutenticado { get; private set; }

    public string? PrincipalId { get; private set; }

    public bool MfaSatisfeito { get; private set; }

    public void Definir(string principalId, bool mfaSatisfeito)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        PrincipalId = principalId;
        EstaAutenticado = true;
        MfaSatisfeito = mfaSatisfeito;
    }

    public void Limpar()
    {
        PrincipalId = null;
        EstaAutenticado = false;
        MfaSatisfeito = false;
    }
}
