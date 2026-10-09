namespace PortalBase.Web.Ui.Navegacao;

/// <summary>
/// Ícones de linha (traço, grade 24×24) do layout: menu lateral e barra superior. Cada nome aponta para o atributo
/// <c>d</c> de um <c>&lt;path&gt;</c> desenhado com <c>stroke="currentColor"</c> (a cor vem do CSS, nos dois temas).
/// Conteúdo fixo do código — nunca vem do usuário; um nome desconhecido cai no ícone neutro.
/// </summary>
public static class IconesLinha
{
    /// <summary>Ícone usado quando o nome não existe no catálogo.</summary>
    public const string Neutro = "ponto";

    private static readonly Dictionary<string, string> Caminhos = new(StringComparer.Ordinal)
    {
        [Neutro] = "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z",

        // Grupos do menu.
        ["grade"] = "M3 3h7v9H3z M14 3h7v5h-7z M14 12h7v9h-7z M3 16h7v5H3z",
        ["fiscal"] = "M4 5h16v15H4z M8 3v4 M16 3v4 M4 10h16 M9 15l2 2 4-4",
        ["banco"] = "M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3z M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6 M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3",
        ["enviar"] = "M22 2L11 13 M22 2l-7 20-4-9-9-4z",
        ["escudo"] = "M12 2l8 4v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6z",
        ["olho"] = "M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z",
        ["pulso"] = "M3 12h4l3-8 4 16 3-8h4",

        // Itens do menu.
        ["calendario"] = "M4 5h16v15H4z M8 3v4 M16 3v4 M4 10h16",
        ["predio"] = "M4 21V5l8-3 8 3v16 M9 21v-4h6v4 M8 8h1 M8 12h1 M15 8h1 M15 12h1",
        ["hierarquia"] = "M9 3h6v4H9z M3 17h6v4H3z M15 17h6v4h-6z M12 7v5 M6 12h12 M6 12v5 M18 12v5",
        ["livro"] = "M4 4h6a3 3 0 0 1 2 1 3 3 0 0 1 2-1h6v15h-6a2 2 0 0 0-2 2 2 2 0 0 0-2-2H4z M12 5v16",
        ["calculadora"] = "M6 2h12a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z M8 6h8v4H8z M8 14h.01 M12 14h.01 M16 14h.01 M8 18h.01 M12 18h.01 M16 18h.01",
        ["validar"] = "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z M8 12l3 3 5-6",
        ["raio"] = "M13 2L4 14h7l-1 8 9-12h-7z",
        ["biblioteca"] = "M4 19.5A2.5 2.5 0 0 1 6.5 17H20 M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z",
        ["cadeado"] = "M5 11h14v10H5z M8 11V7a4 4 0 0 1 8 0v4",
        ["pasta"] = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
        ["importar"] = "M12 3v12 M7 10l5 5 5-5 M4 21h16",
        ["arquivo"] = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6",
        ["planilha"] = "M4 3h16v18H4z M4 9h16 M4 15h16 M10 3v18",
        ["tomada"] = "M9 2v6 M15 2v6 M6 8h12v4a6 6 0 0 1-12 0z M12 18v4",
        ["ciclo"] = "M21 12a9 9 0 0 1-15.5 6.2 M3 12A9 9 0 0 1 18.5 5.8 M18 2v4h-4 M6 22v-4h4",
        ["balanca"] = "M12 3v18 M5 21h14 M6 7h12 M6 7l-3 7a3 3 0 0 0 6 0z M18 7l-3 7a3 3 0 0 0 6 0z",
        ["alerta"] = "M12 3L2 21h20z M12 10v5 M12 18h.01",
        ["rota"] = "M5 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4z M19 9a2 2 0 1 0 0-4 2 2 0 0 0 0 4z M7 17h7a3 3 0 0 0 0-6h-4a3 3 0 0 1 0-6h7",
        ["caixa"] = "M21 8l-9-5-9 5 9 5z M3 8v8l9 5 9-5V8 M12 13v8",
        ["antena"] = "M5 12a7 7 0 0 1 14 0 M8.5 12a3.5 3.5 0 0 1 7 0 M12 12v9 M9 21h6",
        ["engrenagem"] = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M12 2v3 M12 19v3 M4.9 4.9l2.1 2.1 M17 17l2.1 2.1 M2 12h3 M19 12h3 M4.9 19.1L7 17 M17 7l2.1-2.1",
        ["envelope"] = "M4 4h16v16H4z M4 6l8 7 8-7",
        ["recibo"] = "M6 2h12v20l-3-2-3 2-3-2-3 2z M9 7h6 M9 11h6 M9 15h4",
        ["cerquilha"] = "M4 9h16 M4 15h16 M10 3L8 21 M16 3l-2 18",
        ["ferramenta"] = "M14.7 6.3a4 4 0 0 0-5.4 5.4L3 18l3 3 6.3-6.3a4 4 0 0 0 5.4-5.4l-2.6 2.6-2.4-.6-.6-2.4z",
        ["usuarios"] = "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M22 21v-2a4 4 0 0 0-3-3.9 M16 3.1a4 4 0 0 1 0 7.8",
        ["perfis"] = "M12 2l8 4v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6z M9 12l2 2 4-4",
        ["chave"] = "M8 4a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M10.8 10.8L20 20 M16 16l-2 2 M18.5 18.5l-2 2",
        ["coluna"] = "M3 21h18 M5 21V10 M19 21V10 M9 21V10 M15 21V10 M2 10l10-7 10 7z",
        ["certificado"] = "M4 4h16v11H4z M8 8h8 M8 11h5 M14 15l-1 6 3-2 3 2-1-6",
        ["imagem"] = "M3 5h18v14H3z M8.5 10a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3z M21 15l-5-5L5 19",
        ["historico"] = "M3 12a9 9 0 1 0 3-6.7 M3 4v5h5 M12 7v5l3 3",
        ["lupa"] = "M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14z M21 21l-4.3-4.3",
        ["sirene"] = "M7 18v-6a5 5 0 0 1 10 0v6 M5 21h14v-3H5z M12 3v2 M4.2 6.2l1.4 1.4 M19.8 6.2l-1.4 1.4",
        ["robo"] = "M5 8h14v11H5z M12 4v4 M9 13h.01 M15 13h.01 M9 16h6 M2 12v3 M22 12v3",
        ["elo"] = "M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7 M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7",
        ["fila"] = "M8 6h13 M8 12h13 M8 18h13 M3 6h.01 M3 12h.01 M3 18h.01",
        ["saida"] = "M12 15V3 M7 8l5-5 5 5 M4 21h16",

        // Barra superior.
        ["menu"] = "M4 6h16 M4 12h16 M4 18h16",
        ["recolher"] = "M4 6h16 M4 12h10 M4 18h16",
        ["lua"] = "M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z",
        ["sol"] = "M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10z M12 1v2 M12 21v2 M4.2 4.2l1.4 1.4 M18.4 18.4l1.4 1.4 M1 12h2 M21 12h2 M4.2 19.8l1.4-1.4 M18.4 5.6l1.4-1.4",
        ["seta-baixo"] = "M6 9l6 6 6-6",
        ["seta-direita"] = "M9 6l6 6-6 6",
        ["sair"] = "M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4 M16 17l5-5-5-5 M21 12H9",
    };

    /// <summary>Os nomes do catálogo (para testes e documentação).</summary>
    public static IReadOnlyCollection<string> Nomes => Caminhos.Keys;

    /// <summary>O nome existe no catálogo?</summary>
    public static bool Existe(string? nome) => nome is not null && Caminhos.ContainsKey(nome);

    /// <summary>O traço do ícone; nome desconhecido (ou nulo) devolve o ícone neutro.</summary>
    public static string Caminho(string? nome)
        => nome is not null && Caminhos.TryGetValue(nome, out var caminho) ? caminho : Caminhos[Neutro];
}
