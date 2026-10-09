namespace PortalBase.Application.Identidade;

/// <summary>
/// Política de autenticação: proteção contra força bruta (seção 7). Valores conservadores
/// por padrão; o host pode sobrepor via configuração. Lockout por usuário com janela
/// temporal (o rate limiting por origem é responsabilidade do host — seção 32).
/// </summary>
public sealed record PoliticaAutenticacao(int MaxTentativas, TimeSpan DuracaoBloqueio)
{
    public static PoliticaAutenticacao Padrao { get; } = new(5, TimeSpan.FromMinutes(15));
}
