using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Notificacoes;
using PortalBase.Infrastructure.Email;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Email;

/// <summary>
/// Envio REAL por SMTP (MailKit) contra um servidor de teste (Mailpit, Testcontainers): a mensagem chega com remetente,
/// destinatário, assunto e corpo; a senha da autenticação vem do provedor de segredos (cofre); e o modo StartTls NUNCA
/// cai para texto puro — com servidor sem STARTTLS ou com certificado não confiável, o envio falha e nada é entregue.
/// Pulado sem Docker.
/// </summary>
public sealed class EnvioSmtpRealTests
{
    private const string Imagem = "axllent/mailpit:v1.27";

    [SkippableFact]
    public async Task Envia_Com_Autenticacao_E_Senha_Do_Cofre()
    {
        Skip.IfNot(DockerDisponivel.Verificar(), "Docker indisponível neste ambiente.");
        await using var mailpit = await IniciarAsync(tls: false);
        var segredos = new SegredosFalsos("senha-do-cofre");
        var enviador = Enviador(mailpit, OpcoesSmtp.SegurancaNenhuma, usuario: "portal", segredos);

        await enviador.EnviarAsync(new MensagemEmail("maria@exemplo.com", "Redefinição de senha", "Abra: https://portal/x", "<p>Abra</p>"));

        segredos.Pedidos.Should().Equal("email-smtp-senha");
        var mensagens = await MensagensAsync(mailpit);
        mensagens.Should().ContainSingle();
        var resumo = mensagens[0];
        resumo.GetProperty("Subject").GetString().Should().Be("Redefinição de senha");
        resumo.GetProperty("From").GetProperty("Address").GetString().Should().Be("nao-responda@exemplo.com.br");
        resumo.GetProperty("To")[0].GetProperty("Address").GetString().Should().Be("maria@exemplo.com");
        (await TextoAsync(mailpit, resumo.GetProperty("ID").GetString()!)).Should().Contain("https://portal/x");
    }

    [SkippableFact]
    public async Task StartTls_Sem_Suporte_No_Servidor_Falha_Sem_Enviar_Em_Texto_Puro()
    {
        Skip.IfNot(DockerDisponivel.Verificar(), "Docker indisponível neste ambiente.");
        await using var mailpit = await IniciarAsync(tls: false);
        var enviador = Enviador(mailpit, OpcoesSmtp.SegurancaStartTls, usuario: "", new SegredosFalsos("x"));

        var acao = async () => await enviador.EnviarAsync(new MensagemEmail("maria@exemplo.com", "Assunto", "Texto"));

        await acao.Should().ThrowAsync<Exception>();
        (await MensagensAsync(mailpit)).Should().BeEmpty("sem TLS disponível o envio não acontece");
    }

    [SkippableFact]
    public async Task StartTls_Com_Certificado_Nao_Confiavel_Falha()
    {
        Skip.IfNot(DockerDisponivel.Verificar(), "Docker indisponível neste ambiente.");
        await using var mailpit = await IniciarAsync(tls: true);
        var enviador = Enviador(mailpit, OpcoesSmtp.SegurancaStartTls, usuario: "", new SegredosFalsos("x"));

        var acao = async () => await enviador.EnviarAsync(new MensagemEmail("maria@exemplo.com", "Assunto", "Texto"));

        await acao.Should().ThrowAsync<MailKit.Security.SslHandshakeException>("o certificado autoassinado não é aceito");
        (await MensagensAsync(mailpit)).Should().BeEmpty();
    }

    private static async Task<IContainer> IniciarAsync(bool tls)
    {
        var construtor = new ContainerBuilder(Imagem)
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "1")
            .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "1")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/livez")));
        if (tls)
        {
            construtor = construtor
                .WithEnvironment("MP_SMTP_TLS_CERT", "sans:localhost")
                .WithEnvironment("MP_SMTP_TLS_KEY", "sans:localhost");
        }

        var container = construtor.Build();
        await container.StartAsync();
        return container;
    }

    private static EnviadorEmailSmtp Enviador(IContainer mailpit, string seguranca, string usuario, IProvedorSegredos segredos)
        => new(
            Options.Create(new OpcoesEmail
            {
                Modo = OpcoesEmail.ModoSmtp,
                Remetente = { Email = "nao-responda@exemplo.com.br", Nome = "Portal Base" },
                Smtp =
                {
                    Servidor = mailpit.Hostname,
                    Porta = mailpit.GetMappedPublicPort(1025),
                    Seguranca = seguranca,
                    Usuario = usuario,
                    TimeoutSegundos = 15,
                },
            }),
            segredos,
            new RelogioSistemaTeste());

    private static async Task<JsonElement[]> MensagensAsync(IContainer mailpit)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}") };
        var raiz = await http.GetFromJsonAsync<JsonElement>("/api/v1/messages");
        return [.. raiz.GetProperty("messages").EnumerateArray()];
    }

    private static async Task<string> TextoAsync(IContainer mailpit, string id)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}") };
        var mensagem = await http.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}");
        return mensagem.GetProperty("Text").GetString() ?? string.Empty;
    }

    private sealed class SegredosFalsos(string senha) : IProvedorSegredos
    {
        public List<string> Pedidos { get; } = [];

        public ValueTask<string> ObterAsync(string referencia, CancellationToken cancellationToken = default)
        {
            Pedidos.Add(referencia);
            return ValueTask.FromResult(senha);
        }
    }

    private sealed class RelogioSistemaTeste : IRelogio
    {
        public DateTimeOffset AgoraUtc => DateTimeOffset.UtcNow;
    }
}
