using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Notificacoes;
using PortalBase.Domain.Excecoes;
using PortalBase.Domain.Notificacoes;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// Adaptador da tela Configurações → E-mail: lê a configuração vigente, valida com as MESMAS regras da configuração do
/// servidor (<see cref="ValidadorOpcoesEmail"/>), grava a parte não secreta no catálogo (linha única) e faz o envio de
/// teste. Mensagens de negócio — nunca a senha, a exceção ou o texto do servidor SMTP.
/// </summary>
public sealed class ConfiguracaoEmailPortal : IConfiguracaoEmailPortal
{
    private readonly ProvedorOpcoesEmail _provedor;
    private readonly PortalBaseCatalogoDbContext _catalogo;
    private readonly IProvedorSegredos _segredos;
    private readonly IEnviadorEmail _enviador;
    private readonly IRelogio _relogio;

    public ConfiguracaoEmailPortal(
        ProvedorOpcoesEmail provedor, PortalBaseCatalogoDbContext catalogo, IProvedorSegredos segredos, IEnviadorEmail enviador, IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(provedor);
        ArgumentNullException.ThrowIfNull(catalogo);
        ArgumentNullException.ThrowIfNull(segredos);
        ArgumentNullException.ThrowIfNull(enviador);
        ArgumentNullException.ThrowIfNull(relogio);
        _provedor = provedor;
        _catalogo = catalogo;
        _segredos = segredos;
        _enviador = enviador;
        _relogio = relogio;
    }

    public string NomeSegredoSenha => _provedor.NomeSegredoSenha;

    public async Task<ConfiguracaoEmailAtual> ObterAsync(CancellationToken cancellationToken = default)
    {
        await _provedor.RecarregarAsync(cancellationToken).ConfigureAwait(false);
        var daTela = _provedor.DaTela;
        var vigente = _provedor.Value;
        var senhaDefinida = !string.IsNullOrWhiteSpace(vigente.Smtp.SenhaRef)
            && await SegredoExisteAsync(vigente.Smtp.SenhaRef, cancellationToken).ConfigureAwait(false);

        return new ConfiguracaoEmailAtual(
            daTela is null ? OrigemConfiguracaoEmail.Servidor : OrigemConfiguracaoEmail.Tela,
            vigente.Modo,
            vigente.Habilitado,
            vigente.Smtp.Servidor,
            vigente.Smtp.Porta,
            vigente.Smtp.Seguranca,
            vigente.Smtp.Usuario,
            senhaDefinida,
            vigente.Remetente.Email,
            vigente.Remetente.Nome,
            vigente.UrlPublicaPortal,
            daTela?.AtualizadoEmUtc,
            vigente.Aparencia.Tema,
            vigente.Aparencia.CorDestaque,
            vigente.Aparencia.TextoRodape);
    }

    public IReadOnlyList<string> ValidarAparencia(AlteracaoAparenciaEmail aparencia)
    {
        ArgumentNullException.ThrowIfNull(aparencia);
        try
        {
            var candidata = ConfiguracaoEmail.Criar(false, null, 587, null, null, null, null, null, _relogio.AgoraUtc, null);
            candidata.DefinirAparencia(aparencia.Tema, aparencia.CorDestaque, aparencia.TextoRodape, _relogio.AgoraUtc, null);
            return [];
        }
        catch (EntradaInvalidaException excecao)
        {
            return [NomesDosCampos(excecao.Message)];
        }
    }

    public async Task SalvarAparenciaAsync(AlteracaoAparenciaEmail aparencia, Guid? atualizadoPor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aparencia);
        var atual = await _catalogo.ConfiguracoesEmail
            .FirstOrDefaultAsync(c => c.Id == ConfiguracaoEmail.IdUnico, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Não há configuração de e-mail da tela para guardar a aparência.");
        atual.DefinirAparencia(aparencia.Tema, aparencia.CorDestaque, aparencia.TextoRodape, _relogio.AgoraUtc, atualizadoPor);
        await _catalogo.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _provedor.Aplicar(atual);
    }

    public string Previa(AlteracaoAparenciaEmail aparencia, string? urlPublicaPortal)
    {
        ArgumentNullException.ThrowIfNull(aparencia);
        return LayoutEmail.Previa(
            new OpcoesAparenciaEmail
            {
                Tema = aparencia.Tema ?? TemasEmail.Padrao,
                CorDestaque = aparencia.CorDestaque ?? string.Empty,
                TextoRodape = aparencia.TextoRodape ?? string.Empty,
            },
            urlPublicaPortal);
    }

    public IReadOnlyList<string> Validar(AlteracaoConfiguracaoEmail alteracao, bool senhaDefinida)
    {
        ArgumentNullException.ThrowIfNull(alteracao);
        ConfiguracaoEmail candidata;
        try
        {
            candidata = Criar(alteracao, null);
        }
        catch (EntradaInvalidaException excecao)
        {
            return [NomesDosCampos(excecao.Message)];
        }

        if (!candidata.Habilitado)
        {
            return []; // desligado: nada é enviado, os demais campos ficam guardados como estão
        }

        var erros = ValidadorOpcoesEmail.Validar(_provedor.Mesclar(candidata), _provedor.Desenvolvimento)
            .Select(NomesDosCampos)
            .ToList();
        if (!string.IsNullOrWhiteSpace(candidata.Usuario) && string.IsNullOrEmpty(alteracao.NovaSenha) && !senhaDefinida)
        {
            erros.Add("Informe a senha do usuário do SMTP.");
        }

        return erros;
    }

    public async Task SalvarAsync(AlteracaoConfiguracaoEmail alteracao, Guid? atualizadoPor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alteracao);
        var atual = await _catalogo.ConfiguracoesEmail
            .FirstOrDefaultAsync(c => c.Id == ConfiguracaoEmail.IdUnico, cancellationToken)
            .ConfigureAwait(false);
        if (atual is null)
        {
            atual = Criar(alteracao, atualizadoPor);
            _catalogo.ConfiguracoesEmail.Add(atual);
        }
        else
        {
            atual.Atualizar(alteracao.Habilitado, alteracao.Servidor, alteracao.Porta, alteracao.Seguranca, alteracao.Usuario,
                alteracao.RemetenteEmail, alteracao.RemetenteNome, alteracao.UrlPublicaPortal, _relogio.AgoraUtc, atualizadoPor);
        }

        await _catalogo.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _provedor.Aplicar(atual);
    }

    public async Task RemoverAsync(CancellationToken cancellationToken = default)
    {
        var atual = await _catalogo.ConfiguracoesEmail
            .FirstOrDefaultAsync(c => c.Id == ConfiguracaoEmail.IdUnico, cancellationToken)
            .ConfigureAwait(false);
        if (atual is not null)
        {
            _catalogo.ConfiguracoesEmail.Remove(atual);
            await _catalogo.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _provedor.Aplicar(null);
    }

    public bool EnderecoValido(string? email) => ValidadorOpcoesEmail.EnderecoValido(email);

    public async Task<string?> EnviarTesteAsync(string para, CancellationToken cancellationToken = default)
    {
        var url = _provedor.Value.UrlPublicaPortal.TrimEnd('/');
        var botao = string.IsNullOrWhiteSpace(url)
            ? string.Empty
            : $"<a data-acao href=\"{System.Net.WebUtility.HtmlEncode(url + "/conta/entrar")}\">Abrir o Portal Base</a>";
        var mensagem = new MensagemEmail(
            para,
            "Teste de envio — Portal Base",
            "Este é um e-mail de teste do Portal Base. Se ele chegou, o envio de e-mails (SMTP) está funcionando.",
            "<p>Este é um e-mail de teste do <strong>Portal Base</strong>.</p><p>Se ele chegou, o envio de e-mails (SMTP) está funcionando — e é assim que os e-mails do portal vão aparecer com a aparência escolhida.</p>" + botao);
        try
        {
            await _enviador.EnviarAsync(mensagem, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Qualquer falha do transporte vira uma mensagem de negócio para a tela (nunca o texto do servidor).
        catch (Exception excecao)
#pragma warning restore CA1031
        {
            return Traduzir(excecao);
        }
    }

    private ConfiguracaoEmail Criar(AlteracaoConfiguracaoEmail a, Guid? atualizadoPor)
        => ConfiguracaoEmail.Criar(a.Habilitado, a.Servidor, a.Porta, a.Seguranca, a.Usuario, a.RemetenteEmail, a.RemetenteNome,
            a.UrlPublicaPortal, _relogio.AgoraUtc, atualizadoPor);

    private async Task<bool> SegredoExisteAsync(string referencia, CancellationToken cancellationToken)
    {
        try
        {
            return !string.IsNullOrEmpty(await _segredos.ObterAsync(referencia, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            return false; // ausente (ou cofre indisponível): a tela mostra "não definida"
        }
    }

    // As falhas mais comuns em linguagem de negócio. O texto da exceção (que pode citar o destinatário) não sai daqui.
    internal static string Traduzir(Exception excecao) => excecao switch
    {
        AuthenticationException => "O servidor recusou o usuário ou a senha do SMTP.",
        SslHandshakeException => "Não foi possível abrir a conexão segura (TLS): confira a segurança e a porta, ou o certificado do servidor.",
        NotSupportedException => "O servidor não oferece a conexão segura (STARTTLS) exigida. Confira a segurança e a porta.",
        SmtpCommandException comando => $"O servidor de e-mail recusou a mensagem (código {(int)comando.StatusCode}). Confira o remetente e o destinatário.",
        SmtpProtocolException or ServiceNotConnectedException => "O servidor de e-mail encerrou a conversa de forma inesperada. Confira a porta e a segurança.",
        SocketException or TimeoutException or IOException or OperationCanceledException =>
            "Não foi possível conectar ao servidor de e-mail. Confira o endereço, a porta e se a rede libera a saída.",
        InvalidOperationException when excecao.Message.Contains("Segredo", StringComparison.Ordinal) =>
            "A senha do SMTP não está definida no cofre. Informe a senha e salve.",
        InvalidOperationException when excecao.Message.Contains("desligado", StringComparison.Ordinal) => "O envio de e-mail está desligado.",
        _ => $"O envio falhou ({excecao.GetType().Name}).",
    };

    private static string NomesDosCampos(string mensagem) => mensagem
        .Replace("Email:Smtp:Servidor", "Servidor", StringComparison.Ordinal)
        .Replace("Email:Smtp:Porta", "Porta", StringComparison.Ordinal)
        .Replace("Email:Smtp:Seguranca=Nenhuma", "Segurança \"Nenhuma\"", StringComparison.Ordinal)
        .Replace("Email:Smtp:Seguranca", "Segurança", StringComparison.Ordinal)
        .Replace("Email:Smtp:SenhaRef", "A senha", StringComparison.Ordinal)
        .Replace("Email:Smtp:Usuario", "o usuário", StringComparison.Ordinal)
        .Replace("Email:UrlPublicaPortal", "Endereço público do portal", StringComparison.Ordinal)
        .Replace("Email:Remetente:Email", "E-mail do remetente", StringComparison.Ordinal)
        .Replace("'Servidor'", "Servidor", StringComparison.Ordinal)
        .Replace("'Usuario'", "Usuário", StringComparison.Ordinal)
        .Replace("'RemetenteEmail'", "E-mail do remetente", StringComparison.Ordinal)
        .Replace("'RemetenteNome'", "Nome do remetente", StringComparison.Ordinal)
        .Replace("'UrlPublicaPortal'", "Endereço público do portal", StringComparison.Ordinal);
}
