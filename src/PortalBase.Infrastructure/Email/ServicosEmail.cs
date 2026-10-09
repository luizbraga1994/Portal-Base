using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortalBase.Application.Identidade;
using PortalBase.Application.Notificacoes;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Email;

/// <summary>Composição do e-mail transacional (chamada pelo host que dispara e-mails: a Web).</summary>
public static class ServicosEmail
{
    /// <summary>
    /// Registra fila, processador e o transporte do <c>Email:Modo</c>. Configuração inválida FALHA o startup
    /// (fail-fast), com a lista do que corrigir — sem segredo na mensagem.
    /// </summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration, bool desenvolvimento)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var opcoes = new OpcoesEmail();
        configuration.GetSection(OpcoesEmail.Secao).Bind(opcoes);
        var erros = ValidadorOpcoesEmail.Validar(opcoes, desenvolvimento);
        if (erros.Count > 0)
        {
            throw new InvalidOperationException("Configuração de e-mail inválida: " + string.Join(" ", erros));
        }

        // Configuração VIGENTE: a da tela (catálogo), quando existe, ou esta do servidor — lida a cada envio.
        services.TryAddSingleton(sp => new ProvedorOpcoesEmail(
            opcoes,
            desenvolvimento,
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<IOptions<OpcoesVault>>()?.Value.Ambiente ?? "dev",
            sp.GetService<ILogger<ProvedorOpcoesEmail>>()));
        services.AddSingleton<IOptions<OpcoesEmail>>(sp => sp.GetRequiredService<ProvedorOpcoesEmail>());
        services.TryAddSingleton<FilaEmailEmMemoria>();
        services.TryAddSingleton<IFilaEmail>(sp => sp.GetRequiredService<FilaEmailEmMemoria>());
        services.TryAddSingleton<IEstatisticasFilaEmail>(sp => sp.GetRequiredService<FilaEmailEmMemoria>());
        services.TryAddSingleton<ProcessadorFilaEmail>();

        // "Esqueci a senha" depende do e-mail: registrado aqui, só no host que dispara e-mails (a Web). O link usa o
        // endereço público CONFIGURADO (nunca o Host da requisição).
        // O endereço também pode vir da tela: lido A CADA ENVIO (no Blazor o escopo é o circuito inteiro; ler no início
        // do escopo deixava o link com o endereço antigo — vazio ⇒ link relativo que não abre no e-mail).
        services.TryAddScoped(sp =>
        {
            var provedor = sp.GetRequiredService<ProvedorOpcoesEmail>();
            return OpcoesRecuperacaoSenha.Vigente(() => provedor.Value.UrlPublicaPortal);
        });
        services.TryAddScoped<RecuperacaoSenha>();

        // Transporte escolhido NA HORA do envio (SMTP, pasta local ou desligado — a fila recusa quando desligado).
        services.TryAddScoped<IEnviadorEmail, EnviadorEmailConfigurado>();

        // Tela Configurações → E-mail (usa o catálogo do host).
        services.TryAddScoped<IConfiguracaoEmailPortal, ConfiguracaoEmailPortal>();
        services.TryAddScoped<ConfiguracaoEmailPlataforma>();

        return services;
    }
}
