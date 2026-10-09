using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Casos;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Identidade;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Provisionamento;

namespace PortalBase.Application.DependencyInjection;

/// <summary>Composição de DI da camada Application (chamada pelos hosts).</summary>
public static class ServicosApplication
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Casos de uso do catálogo (Prompt 02).
        services.AddScoped<IManipuladorComando<CriarTenantComando, Guid>, CriarTenantManipulador>();

        // Contexto de tenant: MESMA instância por escopo (Scoped), nunca singleton/global (seção 22).
        services.AddScoped<ContextoTenant>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ContextoTenant>());
        services.AddScoped<IContextoTenantMutavel>(sp => sp.GetRequiredService<ContextoTenant>());

        // Resolução/autorização de tenant e execução por tenant (Prompt 03).
        services.AddScoped<IResolvedorTenant, ResolvedorTenant>();
        services.AddScoped<IExecutorTarefaTenant, ExecutorTarefaTenant>();

        // Provisionamento de tenant.
        services.AddScoped<IProvisionadorTenant, ProvisionadorTenant>();

        // Ciclo de vida operacional do tenant (Prompt 09): ativar/desabilitar/reativar.
        services.AddScoped<CicloVidaTenant>();

        // ---- Identidade / autorização (Prompt 04) ----
        // Contexto de segurança: MESMA instância por escopo, deny-by-default (seção 22).
        services.AddScoped<ContextoSeguranca>();
        services.AddScoped<IContextoSeguranca>(sp => sp.GetRequiredService<ContextoSeguranca>());
        services.AddScoped<IContextoSegurancaMutavel>(sp => sp.GetRequiredService<ContextoSeguranca>());

        // Política de lockout (padrão conservador; o host sobrepõe via 'Seguranca:Lockout').
        services.AddSingleton(PoliticaAutenticacao.Padrao);

        // Autorização server-side (RBAC + tenant + MFA, fail-closed).
        services.AddScoped<IServicoAutorizacao, ServicoAutorizacao>();

        // Casos de uso de identidade.
        services.AddScoped<AutenticarUsuario>();
        services.AddScoped<AlterarSenha>();
        services.AddScoped<GestaoMfa>();
        services.AddScoped<GestaoAcesso>();

        // ---- Auditoria / rastreabilidade (Prompt 05) ----
        // Contexto de correlação: MESMA instância por escopo (Web/Worker o preenchem).
        services.AddScoped<ContextoAuditoria>();
        services.AddScoped<IContextoAuditoria>(sp => sp.GetRequiredService<ContextoAuditoria>());
        services.AddScoped<IContextoAuditoriaMutavel>(sp => sp.GetRequiredService<ContextoAuditoria>());
        services.AddSingleton<IPoliticaRetencao, PoliticaRetencao>();
        services.AddScoped<ConsultaAuditoriaAutorizada>();

        // ---- Ingestão de arquivos (Prompt 08) ----
        services.AddScoped<IngerirArquivo>();

        // ---- Read-models autorizados / camada de consultas (Prompt 36) ----
        services.AddScoped<Ingestao.ConsultaArquivosImportacaoAutorizada>();
        services.AddScoped<Incidentes.ConsultaIncidentesAutorizada>();
        services.AddScoped<Observabilidade.ConsultaOperacoesAutorizada>();

        // Read-models ADMINISTRATIVOS/OPERACIONAIS reais (Prompt 71) — tenant.
        services.AddScoped<Administracao.IConsultaTenantAutorizada, Administracao.ConsultaTenantAutorizada>();

        // Commands de administração do tenant e da plataforma.
        services.AddScoped<Administracao.EdicaoCadastroTenant>();
        services.AddScoped<Catalogo.CadastroContribuinte>();
        services.AddScoped<Catalogo.AtualizacaoSchemaContribuintes>();
        services.AddScoped<Identidade.PrimeiroAdministradorPlataforma>();

        // Read-model AUTORIZADO de USUÁRIOS por tenant (Prompt 72). A implementação da consulta
        // (IConsultaUsuariosAdmin) é da Infraestrutura (catálogo). O wrapper aqui aplica admin.usuarios.
        services.AddScoped<Administracao.IConsultaUsuariosAutorizada, Administracao.ConsultaUsuariosAutorizada>();

        return services;
    }
}
