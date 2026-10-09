namespace PortalBase.Shared.Resultados;

/// <summary>Categoria de um erro de aplicação, para mapeamento de resposta/HTTP.</summary>
public enum TipoErro
{
    Validacao,
    NaoAutenticado,
    NaoAutorizado,
    NaoEncontrado,
    Conflito,
    Infraestrutura,
    Inesperado,
}

/// <summary>
/// Erro de aplicação padronizado. A <see cref="Mensagem"/> é segura para exibir
/// ao usuário (nunca contém stack trace, connection string ou segredo — regra 16).
/// </summary>
public sealed record Erro(string Codigo, string Mensagem, TipoErro Tipo)
{
    public static Erro Validacao(string codigo, string mensagem) => new(codigo, mensagem, TipoErro.Validacao);

    public static Erro NaoAutenticado(string codigo, string mensagem) => new(codigo, mensagem, TipoErro.NaoAutenticado);

    public static Erro NaoAutorizado(string codigo, string mensagem) => new(codigo, mensagem, TipoErro.NaoAutorizado);

    public static Erro NaoEncontrado(string codigo, string mensagem) => new(codigo, mensagem, TipoErro.NaoEncontrado);

    public static Erro Conflito(string codigo, string mensagem) => new(codigo, mensagem, TipoErro.Conflito);

    public static Erro Infraestrutura(string codigo, string mensagem) => new(codigo, mensagem, TipoErro.Infraestrutura);

    public static Erro Inesperado(string codigo, string mensagem) => new(codigo, mensagem, TipoErro.Inesperado);
}
