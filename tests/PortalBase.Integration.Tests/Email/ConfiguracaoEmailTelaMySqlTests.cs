using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Notificacoes;
using PortalBase.Infrastructure.Email;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Email;

/// <summary>
/// Configurações → E-mail contra o catálogo real (MySQL): a configuração da tela passa a valer no lugar da do servidor
/// — neste processo na hora e em outro processo na próxima recarga —, o transporte é escolhido no momento do envio
/// (SMTP ou pasta local), a fila liga/desliga sem reiniciar, e "voltar à configuração do servidor" desfaz tudo. Erros
/// de envio viram mensagens de negócio. Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class ConfiguracaoEmailTelaMySqlTests : IDisposable
{
    private readonly FixtureMultiTenant _fixture;
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "portalbase-eml-" + Guid.NewGuid().ToString("N"));

    public ConfiguracaoEmailTelaMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Configuracao_Da_Tela_Vale_No_Lugar_Da_Do_Servidor_E_Pode_Ser_Desfeita()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var servidor = new OpcoesEmail
        {
            Modo = OpcoesEmail.ModoPastaLocal,
            UrlPublicaPortal = "http://localhost:5251",
            Remetente = new OpcoesRemetente { Email = "dev@exemplo.com", Nome = "Portal Base" },
            PastaLocal = new OpcoesPastaLocal { Caminho = _pasta },
        };
        var escopos = _fixture.Servicos.GetRequiredService<IServiceScopeFactory>();
        var provedor = new ProvedorOpcoesEmail(servidor, desenvolvimento: true, escopos, "test");
        var segredos = new SegredosEmMemoria();
        var relogio = new RelogioFixo(DateTimeOffset.UtcNow);
        var fila = new FilaEmailEmMemoria(provedor, NullLogger<FilaEmailEmMemoria>.Instance);

        using var escopo = _fixture.CriarEscopo();
        var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
        var portal = new ConfiguracaoEmailPortal(provedor, catalogo, segredos, new EnviadorEmailConfigurado(provedor, segredos, relogio), relogio);
        await portal.RemoverAsync(); // começa sem configuração de tela

        try
        {
            // 1) Sem configuração de tela: vale a do servidor (pasta local) — o teste vira um .eml.
            var inicial = await portal.ObterAsync();
            inicial.Origem.Should().Be(OrigemConfiguracaoEmail.Servidor);
            inicial.Modo.Should().Be(OpcoesEmail.ModoPastaLocal);
            (await portal.EnviarTesteAsync("maria@exemplo.com")).Should().BeNull();
            Directory.GetFiles(_pasta, "*.eml").Should().ContainSingle();

            // 2) Validação com as regras do servidor, em mensagens de negócio.
            var invalida = new AlteracaoConfiguracaoEmail(true, "servidor com espaço", 587, "StartTls", "portal", null, "não-é-email", "Portal", "ftp://x");
            var erros = portal.Validar(invalida, senhaDefinida: false);
            erros.Should().Contain(e => e.StartsWith("Servidor", StringComparison.Ordinal));
            erros.Should().Contain(e => e.StartsWith("E-mail do remetente", StringComparison.Ordinal));
            erros.Should().Contain(e => e.StartsWith("Endereço público do portal", StringComparison.Ordinal));
            erros.Should().Contain("Informe a senha do usuário do SMTP.");

            // 3) Salva a configuração da tela: passa a valer na hora (SMTP), com a senha pelo nome do cofre.
            var valida = new AlteracaoConfiguracaoEmail(true, "smtp.invalid", 2525, "Nenhuma", "portal", null, "nao-responda@exemplo.com", "Portal Base", "https://portal.exemplo.com");
            portal.Validar(valida with { NovaSenha = "x" }, senhaDefinida: false).Should().BeEmpty();
            segredos.Valores[portal.NomeSegredoSenha] = "senha-smtp";
            await portal.SalvarAsync(valida, Guid.NewGuid());

            var depois = await portal.ObterAsync();
            depois.Origem.Should().Be(OrigemConfiguracaoEmail.Tela);
            depois.Modo.Should().Be(OpcoesEmail.ModoSmtp);
            depois.SenhaDefinida.Should().BeTrue();
            provedor.Value.Smtp.SenhaRef.Should().Be(portal.NomeSegredoSenha);
            provedor.Value.UrlPublicaPortal.Should().Be("https://portal.exemplo.com");
            depois.Tema.Should().Be("Portal", "o tema padrão é o layout do portal");

            // Aparência dos e-mails: validada, gravada no catálogo e em vigor neste processo na hora.
            portal.ValidarAparencia(new AlteracaoAparenciaEmail("Inexistente", null, null)).Should().ContainSingle();
            portal.ValidarAparencia(new AlteracaoAparenciaEmail("Carmim", "vermelho", null)).Should().ContainSingle();
            await portal.SalvarAparenciaAsync(new AlteracaoAparenciaEmail("Carmim", "#c0142a", "Andersen Consulting"), Guid.NewGuid());
            var comAparencia = await portal.ObterAsync();
            comAparencia.Tema.Should().Be("Carmim");
            comAparencia.CorDestaque.Should().Be("#C0142A");
            comAparencia.TextoRodape.Should().Be("Andersen Consulting");
            provedor.Value.Aparencia.Tema.Should().Be("Carmim");
            portal.Previa(new AlteracaoAparenciaEmail("Azul", null, null), comAparencia.UrlPublicaPortal).Should().Contain("Acessar o Portal Base");

            // Outro processo (outra instância) enxerga a mesma configuração na próxima recarga.
            var outraInstancia = new ProvedorOpcoesEmail(servidor, desenvolvimento: true, escopos, "test");
            outraInstancia.Value.Modo.Should().Be(OpcoesEmail.ModoPastaLocal, "antes de recarregar, vale a do servidor");
            await outraInstancia.RecarregarAsync();
            outraInstancia.Value.Smtp.Servidor.Should().Be("smtp.invalid");
            outraInstancia.Value.Aparencia.Tema.Should().Be("Carmim", "a aparência também vale nas outras instâncias");

            // O transporte agora é SMTP: servidor inexistente vira mensagem de negócio (sem o texto da exceção).
            (await portal.EnviarTesteAsync("maria@exemplo.com")).Should().StartWith("Não foi possível conectar ao servidor de e-mail");
            Directory.GetFiles(_pasta, "*.eml").Should().ContainSingle("nada mais foi para a pasta local");

            // 4) Desligar pela tela desliga a fila na hora.
            fila.Habilitado.Should().BeTrue();
            await portal.SalvarAsync(valida with { Habilitado = false }, null);
            fila.Habilitado.Should().BeFalse();
            fila.Enfileirar(new MensagemEmail("maria@exemplo.com", "x", "y")).Should().BeFalse();
        }
        finally
        {
            // 5) Voltar à configuração do servidor.
            await portal.RemoverAsync();
        }

        (await portal.ObterAsync()).Origem.Should().Be(OrigemConfiguracaoEmail.Servidor);
        fila.Habilitado.Should().BeTrue();
        provedor.Value.Modo.Should().Be(OpcoesEmail.ModoPastaLocal);
    }

    [Theory]
    [InlineData("auth", "O servidor recusou o usuário ou a senha do SMTP.")]
    [InlineData("socket", "Não foi possível conectar ao servidor de e-mail. Confira o endereço, a porta e se a rede libera a saída.")]
    [InlineData("segredo", "A senha do SMTP não está definida no cofre. Informe a senha e salve.")]
    [InlineData("outro", "O envio falhou (FormatException).")]
    public void Falhas_De_Envio_Viram_Mensagens_De_Negocio(string caso, string esperado)
    {
        Exception excecao = caso switch
        {
            "auth" => new MailKit.Security.AuthenticationException("535 5.7.3 Authentication unsuccessful for maria@exemplo.com"),
            "socket" => new System.Net.Sockets.SocketException(11001),
            "segredo" => new InvalidOperationException("Segredo 'portalbase-test-email-smtp-password' não configurado."),
            _ => new FormatException("detalhe interno"),
        };

        var mensagem = ConfiguracaoEmailPortal.Traduzir(excecao);

        mensagem.Should().Be(esperado);
        mensagem.Should().NotContain("maria@exemplo.com").And.NotContain("detalhe interno");
    }

    private sealed class SegredosEmMemoria : IProvedorSegredos
    {
        public Dictionary<string, string> Valores { get; } = new(StringComparer.Ordinal);

        public ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default)
            => Valores.TryGetValue(referencia, out var valor)
                ? ValueTask.FromResult(valor)
                : throw new InvalidOperationException($"Segredo '{referencia}' não configurado.");
    }
}
