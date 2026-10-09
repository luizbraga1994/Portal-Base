using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using PortalBase.AcessoLocal;

namespace PortalBase.Integration.Tests.Ferramentas;

/// <summary>
/// Terminal falso do comando de acesso local: responde às perguntas por filas, registra TUDO o que o comando
/// escreve (inclusive os rótulos das perguntas) e captura a chave TOTP exibida — para calcular o código como um
/// app autenticador faria e para provar que a senha jamais aparece na saída.
/// </summary>
internal sealed class TerminalFalso : ITerminal
{
    private const string PrefixoChave = "  chave : ";

    private readonly Queue<string?> _respostas = new();
    private readonly Queue<string?> _respostasOcultas = new();
    private readonly List<string> _saida = [];

    public TerminalFalso(bool interativo = true) => Interativo = interativo;

    public bool Interativo { get; }

    /// <summary>Linha devolvida por <see cref="LerLinha"/> (simula <c>--senha-stdin</c>).</summary>
    public string? LinhaEntrada { get; init; }

    /// <summary>Gera a resposta à pergunta do código MFA (padrão: código correto calculado da chave exibida).</summary>
    public Func<TerminalFalso, string?> ResponderCodigoMfa { get; init; } =
        terminal => CodigoTotp(terminal.ChaveTotpExibida!, DateTimeOffset.UtcNow);

    public string? ChaveTotpExibida { get; private set; }

    public IReadOnlyList<string> Saida => _saida;

    public string Texto => string.Join('\n', _saida);

    public int PerguntasMfa { get; private set; }

    public TerminalFalso Responder(params string?[] respostas)
    {
        foreach (var resposta in respostas)
        {
            _respostas.Enqueue(resposta);
        }

        return this;
    }

    public TerminalFalso ResponderOculto(params string?[] respostas)
    {
        foreach (var resposta in respostas)
        {
            _respostasOcultas.Enqueue(resposta);
        }

        return this;
    }

    /// <summary>Códigos de recuperação exibidos após a ativação do MFA (linhas indentadas após o aviso).</summary>
    public IReadOnlyList<string> CodigosRecuperacaoExibidos()
    {
        var inicio = _saida.FindIndex(l => l.Contains("Códigos de RECUPERAÇÃO", StringComparison.Ordinal));
        return inicio < 0
            ? []
            : _saida.Skip(inicio + 1).TakeWhile(l => l.StartsWith("    ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
    }

    public void Escrever(string texto)
    {
        _saida.Add(texto);
        if (texto.StartsWith(PrefixoChave, StringComparison.Ordinal))
        {
            ChaveTotpExibida = texto[PrefixoChave.Length..].Replace(" ", string.Empty, StringComparison.Ordinal);
        }
    }

    public void Aviso(string texto) => _saida.Add("[aviso] " + texto);

    public void Erro(string texto) => _saida.Add("[erro] " + texto);

    public string? Perguntar(string rotulo)
    {
        _saida.Add(rotulo + ": ");
        if (rotulo.StartsWith("Código", StringComparison.Ordinal))
        {
            PerguntasMfa++;
            return ResponderCodigoMfa(this);
        }

        return _respostas.TryDequeue(out var resposta) ? resposta : null;
    }

    // A resposta NÃO é registrada na saída: é exatamente o que um terminal sem eco faz.
    public string? PerguntarOculto(string rotulo)
    {
        _saida.Add(rotulo + ": ");
        return _respostasOcultas.TryDequeue(out var resposta) ? resposta : null;
    }

    public string? LerLinha() => LinhaEntrada;

    /// <summary>TOTP RFC 6238 (HMAC-SHA1, 6 dígitos, 30 s) — o que o app autenticador mostraria.</summary>
    public static string CodigoTotp(string segredoBase32, DateTimeOffset agoraUtc)
    {
        const string Alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int acumulado = 0, bits = 0;
        foreach (var caractere in segredoBase32.TrimEnd('='))
        {
            acumulado = (acumulado << 5) | Alfabeto.IndexOf(char.ToUpperInvariant(caractere), StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((acumulado >> bits) & 0xFF));
            }
        }

        var contador = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(contador, agoraUtc.ToUnixTimeSeconds() / 30);
#pragma warning disable CA5350 // SHA1 é mandatório pela RFC 6238 (mesmo algoritmo do ProvedorTotpRfc6238).
        var hash = HMACSHA1.HashData(bytes.ToArray(), contador);
#pragma warning restore CA5350
        var deslocamento = hash[^1] & 0x0F;
        var binario = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(deslocamento, 4)) & 0x7FFFFFFF;
        return (binario % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
