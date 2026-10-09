using PortalBase.Domain.Comum;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Notificacoes;

/// <summary>
/// Configuração do e-mail de disparo (SMTP) definida PELA TELA da plataforma — linha única no catálogo. Quando existe,
/// vale no lugar da seção <c>Email</c> do appsettings. Guarda só dados NÃO sensíveis: a senha do SMTP fica no cofre
/// (regra 7). Invariantes básicas aqui (obrigatórios, limites, porta, modo de segurança); o formato do endereço, do
/// servidor e da URL é conferido na fronteira, com as mesmas regras da configuração do servidor.
/// </summary>
public sealed class ConfiguracaoEmail
{
    public const string SegurancaStartTls = "StartTls";
    public const string SegurancaSslTls = "SslTls";
    public const string SegurancaNenhuma = "Nenhuma";

    public const int TamanhoMaximoServidor = 253;
    public const int TamanhoMaximoUsuario = 256;
    public const int TamanhoMaximoEmail = 254;
    public const int TamanhoMaximoNome = 100;
    public const int TamanhoMaximoUrl = 300;
    public const int TamanhoMaximoTema = 30;
    public const int TamanhoMaximoTextoRodape = 300;

    /// <summary>Id fixo da linha única — torna a gravação determinística e idempotente (regra 12).</summary>
    public static readonly Guid IdUnico = Guid.Parse("00000000-0000-0000-0000-00000000e3a1");

    private ConfiguracaoEmail()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Envio ligado? Desligado = nenhum e-mail sai (nem "Esqueci a senha").</summary>
    public bool Habilitado { get; private set; }

    public string Servidor { get; private set; } = string.Empty;

    public int Porta { get; private set; }

    public string Seguranca { get; private set; } = SegurancaStartTls;

    /// <summary>Usuário da autenticação SMTP; vazio = sem autenticação (relay interno).</summary>
    public string Usuario { get; private set; } = string.Empty;

    public string RemetenteEmail { get; private set; } = string.Empty;

    public string RemetenteNome { get; private set; } = string.Empty;

    /// <summary>Endereço público do portal (base dos links dos e-mails).</summary>
    public string UrlPublicaPortal { get; private set; } = string.Empty;

    /// <summary>Tema visual dos e-mails (<see cref="TemasEmail"/>); o padrão segue o layout do portal.</summary>
    public string Tema { get; private set; } = TemasEmail.Padrao;

    /// <summary>Cor de destaque (#RRGGBB) no lugar da cor do tema; vazio = a do tema.</summary>
    public string CorDestaque { get; private set; } = string.Empty;

    /// <summary>Texto livre do rodapé dos e-mails (ex.: nome da empresa e telefone); vazio = só o padrão.</summary>
    public string TextoRodape { get; private set; } = string.Empty;

    public DateTimeOffset AtualizadoEmUtc { get; private set; }

    public Guid? AtualizadoPorUsuarioId { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public static ConfiguracaoEmail Criar(
        bool habilitado, string? servidor, int porta, string? seguranca, string? usuario, string? remetenteEmail,
        string? remetenteNome, string? urlPublicaPortal, DateTimeOffset agoraUtc, Guid? atualizadoPor)
    {
        var configuracao = new ConfiguracaoEmail { Id = IdUnico };
        configuracao.Atualizar(habilitado, servidor, porta, seguranca, usuario, remetenteEmail, remetenteNome, urlPublicaPortal, agoraUtc, atualizadoPor);
        return configuracao;
    }

    /// <summary>Substitui a configuração (mesma linha). Ligado exige servidor, porta, segurança, remetente e URL.</summary>
    /// <exception cref="EntradaInvalidaException">Campo obrigatório ausente, acima do limite, porta ou segurança inválida.</exception>
    public void Atualizar(
        bool habilitado, string? servidor, int porta, string? seguranca, string? usuario, string? remetenteEmail,
        string? remetenteNome, string? urlPublicaPortal, DateTimeOffset agoraUtc, Guid? atualizadoPor)
    {
        var servidorLimpo = Limitar(servidor, TamanhoMaximoServidor, "Servidor");
        var usuarioLimpo = Limitar(usuario, TamanhoMaximoUsuario, "Usuario");
        var remetente = Limitar(remetenteEmail, TamanhoMaximoEmail, "RemetenteEmail");
        var nome = Limitar(remetenteNome, TamanhoMaximoNome, "RemetenteNome");
        var url = Limitar(urlPublicaPortal, TamanhoMaximoUrl, "UrlPublicaPortal");
        var modoSeguranca = string.IsNullOrWhiteSpace(seguranca) ? SegurancaStartTls : seguranca.Trim();

        if (modoSeguranca is not (SegurancaStartTls or SegurancaSslTls or SegurancaNenhuma))
        {
            throw new EntradaInvalidaException("A segurança da conexão deve ser StartTls, SslTls ou Nenhuma.");
        }

        if (habilitado)
        {
            _ = Exigir.TextoObrigatorio(servidorLimpo, "Servidor");
            _ = Exigir.TextoObrigatorio(remetente, "RemetenteEmail");
            _ = Exigir.TextoObrigatorio(url, "UrlPublicaPortal");
            if (porta is < 1 or > 65535)
            {
                throw new EntradaInvalidaException("A porta deve estar entre 1 e 65535.");
            }
        }

        Habilitado = habilitado;
        Servidor = servidorLimpo;
        Porta = porta is >= 1 and <= 65535 ? porta : 587;
        Seguranca = modoSeguranca;
        Usuario = usuarioLimpo;
        RemetenteEmail = remetente;
        RemetenteNome = string.IsNullOrEmpty(nome) ? "Portal Base" : nome;
        UrlPublicaPortal = url.TrimEnd('/');
        AtualizadoEmUtc = agoraUtc;
        AtualizadoPorUsuarioId = atualizadoPor;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>Troca a aparência dos e-mails (tema, cor de destaque e texto do rodapé), sem mexer no envio.</summary>
    /// <exception cref="EntradaInvalidaException">Tema desconhecido, cor fora de #RRGGBB ou texto acima do limite.</exception>
    public void DefinirAparencia(string? tema, string? corDestaque, string? textoRodape, DateTimeOffset agoraUtc, Guid? atualizadoPor)
    {
        var codigo = string.IsNullOrWhiteSpace(tema) ? TemasEmail.Padrao : tema.Trim();
        if (!TemasEmail.Existe(codigo))
        {
            throw new EntradaInvalidaException("Tema de e-mail desconhecido.");
        }

        var cor = corDestaque?.Trim() ?? string.Empty;
        if (cor.Length > 0 && !CorValida(cor))
        {
            throw new EntradaInvalidaException("A cor de destaque deve estar no formato #RRGGBB (ex.: #9E1B32).");
        }

        var rodape = Limitar(textoRodape, TamanhoMaximoTextoRodape, "TextoRodape");
        if (rodape.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
        {
            throw new EntradaInvalidaException("O texto do rodapé tem caracteres inválidos.");
        }

        Tema = codigo;
        CorDestaque = cor.ToUpperInvariant();
        TextoRodape = rodape.Replace("\r\n", "\n", StringComparison.Ordinal);
        AtualizadoEmUtc = agoraUtc;
        AtualizadoPorUsuarioId = atualizadoPor;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>#RRGGBB (hexadecimal).</summary>
    public static bool CorValida(string? cor)
        => cor is { Length: 7 } && cor[0] == '#' && cor.Skip(1).All(char.IsAsciiHexDigit);

    private static string Limitar(string? valor, int limite, string campo)
    {
        var limpo = valor?.Trim() ?? string.Empty;
        return limpo.Length <= limite
            ? limpo
            : throw new EntradaInvalidaException($"O campo '{campo}' excede o tamanho máximo de {limite} caracteres.");
    }
}
