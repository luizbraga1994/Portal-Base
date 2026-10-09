using System.Text;

namespace PortalBase.AcessoLocal;

/// <summary>
/// Terminal real. É a ÚNICA exceção ao banimento de <c>Console.Write*</c> (BannedSymbols.txt, regras 8/16): aquele
/// banimento existe para código de servidor/biblioteca, cuja saída deve ir para log com redação; aqui o console É a
/// interface de uma ferramenta interativa de desenvolvimento. A senha é lida sem eco e nunca escrita. O segredo
/// TOTP e os códigos de recuperação aparecem UMA vez, no próprio terminal de quem cadastra o MFA (é assim que um
/// autenticador é configurado), e nunca vão para log.
/// </summary>
public sealed class TerminalConsole : ITerminal
{
    public bool Interativo => !Console.IsInputRedirected;

#pragma warning disable RS0030 // Ferramenta interativa: o console é a interface com o usuário (ver resumo da classe).
    public void Escrever(string texto) => Console.WriteLine(texto);

    public void Aviso(string texto) => Console.WriteLine("[aviso] " + texto);

    public void Erro(string texto) => Console.Error.WriteLine("[erro] " + texto);

    public string? Perguntar(string rotulo)
    {
        Console.Write(rotulo + ": ");
        return Console.ReadLine()?.Trim();
    }

    // UTF-8 explícito (com ou sem BOM): a página de código do console do Windows (850/1252) não decide como a senha
    // é lida. O scripts/criar-acesso-local.ps1 envia em UTF-8 pelo mesmo motivo.
    public string? LerLinha()
    {
        using var leitor = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        return leitor.ReadLine();
    }

    public string? PerguntarOculto(string rotulo)
    {
        Console.Write(rotulo + ": ");
        if (!Interativo)
        {
            return Console.ReadLine();
        }

        var valor = new StringBuilder();
        while (true)
        {
            var tecla = Console.ReadKey(intercept: true); // sem eco
            if (tecla.Key == ConsoleKey.Enter)
            {
                Console.WriteLine(string.Empty);
                return valor.ToString();
            }

            if (tecla.Key == ConsoleKey.Backspace)
            {
                if (valor.Length > 0)
                {
                    valor.Length--;
                }

                continue;
            }

            if (!char.IsControl(tecla.KeyChar))
            {
                valor.Append(tecla.KeyChar);
            }
        }
    }
#pragma warning restore RS0030
}
