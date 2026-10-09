namespace PortalBase.Domain.Notificacoes;

/// <summary>Tema visual dos e-mails do portal (código gravado na configuração e nome mostrado na tela).</summary>
public sealed record TemaEmail(string Codigo, string Nome, string Descricao);

/// <summary>
/// Os temas disponíveis para os e-mails do portal. O padrão segue o layout do portal (azul-marinho e carmim).
/// As cores de cada tema ficam na montagem do e-mail (Infraestrutura); aqui só os códigos válidos.
/// </summary>
public static class TemasEmail
{
    public const string Padrao = "Portal";

    public static IReadOnlyList<TemaEmail> Todos { get; } =
    [
        new(Padrao, "Portal (padrão)", "Cores do portal: cabeçalho azul-marinho, botões em carmim, fundo claro."),
        new("PortalEscuro", "Portal escuro", "O mesmo visual do portal no modo escuro."),
        new("Carmim", "Carmim institucional", "Branco com faixa e destaques em vermelho."),
        new("Azul", "Azul corporativo", "Cabeçalho azul e botões azuis, fundo azul bem claro."),
        new("Neutro", "Neutro", "Preto e branco, sem cores de marca."),
    ];

    public static bool Existe(string? codigo)
        => !string.IsNullOrWhiteSpace(codigo) && Todos.Any(t => string.Equals(t.Codigo, codigo.Trim(), StringComparison.Ordinal));

    /// <summary>O tema informado, ou o padrão quando vazio/desconhecido (nunca falha na hora de montar um e-mail).</summary>
    public static TemaEmail Obter(string? codigo)
        => Todos.FirstOrDefault(t => string.Equals(t.Codigo, codigo?.Trim(), StringComparison.Ordinal)) ?? Todos[0];
}
