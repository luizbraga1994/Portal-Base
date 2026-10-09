using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortalBase.Application.Segredos;
using PortalBase.Domain.Notificacoes;
using PortalBase.Infrastructure.Persistencia.Catalogo;

namespace PortalBase.Infrastructure.Email;

/// <summary>
/// A configuração de e-mail EM VIGOR: a da tela (Configurações → E-mail, no catálogo) quando existe; senão, a seção
/// <c>Email</c> do servidor. Exposta como <see cref="IOptions{TOptions}"/>: quem lê <c>Value</c> na hora do envio pega
/// sempre a vigente — sem reiniciar o portal. A tela aplica a mudança neste processo na hora
/// (<see cref="Aplicar"/>); os outros processos/instâncias releem a cada minuto (<see cref="RecarregarAsync"/>).
/// Falha ao ler o catálogo nunca derruba o envio: fica a última configuração conhecida.
/// </summary>
public sealed partial class ProvedorOpcoesEmail : IOptions<OpcoesEmail>
{
    private readonly OpcoesEmail _servidor;
    private readonly IServiceScopeFactory? _escopos;
    private readonly ILogger<ProvedorOpcoesEmail>? _logger;
    private volatile Estado _estado;

    public ProvedorOpcoesEmail(
        OpcoesEmail servidor, bool desenvolvimento, IServiceScopeFactory? escopos = null, string ambienteCofre = "dev",
        ILogger<ProvedorOpcoesEmail>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(servidor);
        _servidor = servidor;
        _escopos = escopos;
        _logger = logger;
        Desenvolvimento = desenvolvimento;
        NomeSegredoSenha = NomeSegredo.EmailSmtpSenha(ambienteCofre);
        _estado = new Estado(servidor, null);
    }

    /// <summary>O host roda em Development/Testing (regras mais brandas: http, SMTP sem TLS, pasta local).</summary>
    public bool Desenvolvimento { get; }

    /// <summary>Nome (cofre) da senha do SMTP definida pela tela.</summary>
    public string NomeSegredoSenha { get; }

    /// <summary>A configuração vigente.</summary>
    public OpcoesEmail Value => _estado.Opcoes;

    /// <summary>A configuração definida pela tela, se houver (nulo = vale a do servidor).</summary>
    public ConfiguracaoEmail? DaTela => _estado.DaTela;

    /// <summary>A configuração do servidor (appsettings), sempre a base.</summary>
    public OpcoesEmail DoServidor => _servidor;

    /// <summary>Passa a valer a configuração da tela informada (ou a do servidor, com nulo).</summary>
    public void Aplicar(ConfiguracaoEmail? daTela) => _estado = new Estado(Mesclar(daTela), daTela);

    /// <summary>Relê a configuração da tela no catálogo. Sem catálogo (ou com erro), mantém a vigente.</summary>
    public async Task RecarregarAsync(CancellationToken cancellationToken = default)
    {
        if (_escopos is null)
        {
            return;
        }

        try
        {
            await using var escopo = _escopos.CreateAsyncScope();
            var catalogo = escopo.ServiceProvider.GetService<PortalBaseCatalogoDbContext>();
            if (catalogo is null)
            {
                return;
            }

            var daTela = await catalogo.ConfiguracoesEmail.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == ConfiguracaoEmail.IdUnico, cancellationToken)
                .ConfigureAwait(false);
            Aplicar(daTela);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            if (_logger is not null)
            {
                LogFalhaRecarga(_logger, excecao.GetType().Name);
            }
        }
    }

    /// <summary>A configuração vigente para uma configuração de tela (a do servidor completa o que a tela não define).</summary>
    public OpcoesEmail Mesclar(ConfiguracaoEmail? daTela)
    {
        if (daTela is null)
        {
            return _servidor;
        }

        return new OpcoesEmail
        {
            Modo = daTela.Habilitado ? OpcoesEmail.ModoSmtp : OpcoesEmail.ModoDesligado,
            UrlPublicaPortal = daTela.UrlPublicaPortal,
            Remetente = new OpcoesRemetente { Email = daTela.RemetenteEmail, Nome = daTela.RemetenteNome },
            Aparencia = new OpcoesAparenciaEmail { Tema = daTela.Tema, CorDestaque = daTela.CorDestaque, TextoRodape = daTela.TextoRodape },
            Smtp = new OpcoesSmtp
            {
                Servidor = daTela.Servidor,
                Porta = daTela.Porta,
                Seguranca = daTela.Seguranca,
                Usuario = daTela.Usuario,
                SenhaRef = NomeSegredoSenha,
                TimeoutSegundos = _servidor.Smtp.TimeoutSegundos,
                TentativasMaximas = _servidor.Smtp.TentativasMaximas,
            },
            PastaLocal = _servidor.PastaLocal,
        };
    }

    [LoggerMessage(EventId = 7120, Level = LogLevel.Warning, Message = "Configuração de e-mail da tela não pôde ser relida ({TipoErro}); continua valendo a última conhecida.")]
    private static partial void LogFalhaRecarga(ILogger logger, string tipoErro);

    private sealed record Estado(OpcoesEmail Opcoes, ConfiguracaoEmail? DaTela);
}
