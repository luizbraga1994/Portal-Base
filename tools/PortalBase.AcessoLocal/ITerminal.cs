namespace PortalBase.AcessoLocal;

/// <summary>
/// Diálogo com quem executa o comando. O núcleo (<see cref="CriadorAcessoLocal"/>) nunca usa <c>Console</c>
/// diretamente: nos testes, um terminal falso responde às perguntas e permite provar que a senha jamais é escrita.
/// </summary>
public interface ITerminal
{
    /// <summary>Há uma pessoa no teclado (entrada padrão não redirecionada).</summary>
    bool Interativo { get; }

    void Escrever(string texto);

    void Aviso(string texto);

    void Erro(string texto);

    /// <summary>Pergunta COM eco (somente dados não secretos).</summary>
    string? Perguntar(string rotulo);

    /// <summary>Pergunta SEM eco (senha). O valor nunca volta para a saída.</summary>
    string? PerguntarOculto(string rotulo);

    /// <summary>Lê uma linha da entrada padrão, sem rótulo e sem eco (<c>--senha-stdin</c>).</summary>
    string? LerLinha();
}
