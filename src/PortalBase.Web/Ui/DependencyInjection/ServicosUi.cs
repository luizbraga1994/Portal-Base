using Microsoft.Extensions.DependencyInjection;
using PortalBase.Web.Seguranca;
using PortalBase.Web.Ui.Autorizacao;
using PortalBase.Web.Ui.Contexto;
using PortalBase.Web.Ui.Dados;
using PortalBase.Web.Ui.Notificacoes;

namespace PortalBase.Web.Ui.DependencyInjection;

/// <summary>Registra os serviços de APRESENTAÇÃO do Portal Base (sem regra de negócio).</summary>
public static class ServicosUi
{
    public static IServiceCollection AddUiPortalBase(this IServiceCollection servicos)
    {
        ArgumentNullException.ThrowIfNull(servicos);

        // Autorização VISUAL (Prompt 71, F70-CRIT-01): projeção REAL das permissões do usuário.
        // IContextoTenantAtual reusa a MESMA instância de SelecaoTenant (registrada em ConfiguracaoSeguranca).
        servicos.AddScoped<IContextoTenantAtual>(sp => sp.GetRequiredService<SelecaoTenant>());
        servicos.AddScoped<IProvedorPermissoesVisuais, ProvedorPermissoesVisuais>();
        servicos.AddScoped<IAutorizacaoVisual, AutorizacaoVisual>();
        servicos.AddScoped<IContextoApresentacao, ContextoApresentacao>();
        servicos.AddScoped<IDadosAuditoria, DadosAuditoria>();

        // Arquivos do tenant: consulta (read-model autorizado) + envio seguro (embrulha o caso de uso IngerirArquivo).
        servicos.AddScoped<IDadosArquivos, DadosArquivos>();
        servicos.AddScoped<IDadosEnvioArquivo, DadosEnvioArquivo>();

        // Administração e Operações (Prompt 40) — read-models de PLATAFORMA (Incidentes/Operações).
        servicos.AddScoped<IDadosIncidentes, DadosIncidentes>();
        servicos.AddScoped<IDadosOperacoes, DadosOperacoes>();
        servicos.AddScoped<IDadosBackups, DadosBackups>();
        servicos.AddScoped<IDadosAcoesIncidentes, DadosAcoesIncidentes>();

        // Administração REAL (Prompt 71/72) — tenant corrente + usuários por tenant e ações (GestaoAcesso, CicloVidaTenant).
        servicos.AddScoped<IDadosTenantAdmin, DadosTenantAdmin>();
        servicos.AddScoped<IDadosUsuariosAdmin, DadosUsuariosAdmin>();
        servicos.AddScoped<IDadosAcoesUsuario, DadosAcoesUsuario>();
        servicos.AddScoped<IDadosAcoesTenant, DadosAcoesTenant>();

        // Cadastro de usuários e perfis (cadastrar/alterar/excluir, perfis personalizados, Administrador da plataforma).
        // Os casos de uso ficam na Web porque o link de senha usa o e-mail de disparo (só a Web envia e-mail).
        servicos.AddScoped<PortalBase.Application.Identidade.GestaoUsuarios>();
        servicos.AddScoped<PortalBase.Application.Identidade.GestaoPerfis>();
        servicos.AddScoped<PortalBase.Application.Identidade.GestaoContribuintesDoUsuario>();
        servicos.AddScoped<IDadosGestaoUsuarios, DadosGestaoUsuarios>();
        servicos.AddScoped<IDadosPerfis, DadosPerfis>();

        // Acrescente aqui as fontes de dados (adaptadores de UI) das telas do seu domínio.

        servicos.AddScoped<ServicoNotificacoes>();

        return servicos;
    }
}
