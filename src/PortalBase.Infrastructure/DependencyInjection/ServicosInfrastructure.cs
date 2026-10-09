using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Catalogo;
using PortalBase.Application.Identidade;
using PortalBase.Application.Ingestao;
using PortalBase.Application.Segredos;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Provisionamento;
using PortalBase.Infrastructure.Auditoria;
using PortalBase.Infrastructure.Ingestao;
using PortalBase.Infrastructure.Multitenancy;
using PortalBase.Infrastructure.Opcoes;
using PortalBase.Infrastructure.Persistencia;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Infrastructure.Provisionamento;
using PortalBase.Infrastructure.Repositorios;
using PortalBase.Infrastructure.Segredos;
using PortalBase.Infrastructure.Seguranca;
using PortalBase.Infrastructure.Tempo;

namespace PortalBase.Infrastructure.DependencyInjection;

/// <summary>Composição de DI da Infrastructure (chamada pelos hosts).</summary>
public static class ServicosInfrastructure
{
    /// <summary>Nome da connection string do banco de CATÁLOGO central.</summary>
    public const string NomeConexaoCatalogo = "Catalogo";

    /// <param name="conexaoObrigatoria">
    /// Quando <c>true</c> (Produção), a ausência da connection string do catálogo falha
    /// o startup (fail-closed). Fora de Produção, registra um placeholder não-conectável
    /// (readiness reportará indisponível). Isto é o banco CENTRAL do SaaS — o banco do
    /// contribuinte NUNCA é aberto aqui (seções 16, 17): sem tenant resolvido não há
    /// conexão de tenant (Prompt 03).
    /// </param>
    /// <param name="producao">
    /// Sinaliza ambiente de Produção para o gerenciamento de segredos (Prompt 06): em Produção,
    /// exige-se cofre gerenciado (Vault:Modo=KeyVault) — o provedor local de Dev/Test é proibido
    /// (fail-fast). Independe de <paramref name="conexaoObrigatoria"/> (que rege o catálogo).
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool conexaoObrigatoria,
        bool producao = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var conexao = configuration.GetConnectionString(NomeConexaoCatalogo);

        if (string.IsNullOrWhiteSpace(conexao))
        {
            if (conexaoObrigatoria)
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{NomeConexaoCatalogo} é obrigatória em Produção (banco de catálogo).");
            }

            conexao = "server=localhost;database=portalbase_catalogo_dev";
        }

        // P62-R1: o descarte de um contexto (fim do escopo/circuito) aguarda os comandos/leitores em voo — nunca devolve
        // ao pool do MySql.Data um canal no meio de uma leitura (ver ProtecaoOperacaoEmVoo).
        services.AddSingleton<ProtecaoOperacaoEmVoo>();

        // Senha do catálogo pelo cofre (BancoCatalogo:SenhaRef): colocada na conexão só quando ela abre, de forma
        // assíncrona e servida pelo cache do provedor (INT-002) — criar o contexto não lê o cofre.
        var senhaCatalogoRef = configuration[ConexaoCatalogo.ChaveSenhaRef]?.Trim();
        ConexaoCatalogo.Validar(conexao, senhaCatalogoRef);
        if (!string.IsNullOrEmpty(senhaCatalogoRef))
        {
            services.AddSingleton(sp => new SenhaConexaoCatalogo(sp.GetRequiredService<IProvedorSegredos>(), conexao, senhaCatalogoRef));
        }

        services.AddDbContext<PortalBaseCatalogoDbContext>((servicos, opcoes) =>
        {
            opcoes.UseMySQL(conexao);
            if (servicos.GetService<SenhaConexaoCatalogo>() is { } senhaCatalogo)
            {
                opcoes.AddInterceptors(senhaCatalogo);
            }

            opcoes.AddInterceptors(servicos.GetRequiredService<ProtecaoOperacaoEmVoo>());
        });

        services.AddSingleton<IRelogio, RelogioSistema>();

        // Auditoria (Prompt 05): sink append-only enriquecido + integridade + consulta.
        services.Configure<OpcoesAuditoria>(configuration.GetSection(OpcoesAuditoria.Secao));
        services.AddSingleton<IntegridadeAuditoria>();
        services.AddScoped<IAuditoria, AuditoriaEfCore>();
        services.AddScoped<IConsultaAuditoria, ConsultaAuditoriaEfCore>();
        services.AddScoped<IConsultaNomesAtores, ConsultaNomesAtoresCatalogo>();

        // Governança de INCIDENTES de segurança (Prompt 30, seções 45-47) — catálogo (plataforma), append-only.
        services.AddScoped<Application.Incidentes.IRepositorioIncidentes, Incidentes.RepositorioIncidentesCatalogo>();
        services.AddScoped<Application.Incidentes.ServicoIncidentes>();

        // Observabilidade / operações de produção (Prompt 31) — registros em memória (saúde de workers e última
        // integração), métricas operacionais (cardinalidade baixa), health checks e a ponte alerta→incidente.
        // Nunca PII/payload/segredo (§3/§80).
        services.Configure<Application.Observabilidade.OpcoesObservabilidade>(configuration.GetSection(Application.Observabilidade.OpcoesObservabilidade.Secao));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<Application.Observabilidade.OpcoesObservabilidade>>().Value);
        services.AddSingleton<Application.Observabilidade.IRegistroSaudeWorkers, Observabilidade.RegistroSaudeWorkers>();
        services.AddSingleton<Application.Observabilidade.IRegistroUltimaIntegracao, Observabilidade.RegistroUltimaIntegracao>();
        // Saúde gravada pelo host do Worker no catálogo, lida pela Web em Operações (processos distintos).
        services.AddScoped<Application.Observabilidade.IRepositorioSaudeWorkersPersistida, Observabilidade.RepositorioSaudeWorkersPersistida>();
        services.AddSingleton<Observabilidade.MetricasOperacionais>();
        services.AddScoped<Application.Observabilidade.ServicoPonteAlertaIncidente>();

        // Fonte da rotina de alertas (OBS-004): espaço dos discos das pastas do portal.
        services.AddSingleton<Application.Observabilidade.IVerificadorEspacoDisco>(_ => Observabilidade.VerificadorEspacoDisco.DaConfiguracao(configuration));

        services.AddScoped<Saude.VerificacaoVivacidade>();
        services.AddScoped<Saude.VerificacaoSaudeWorkers>();
        services.AddScoped<Saude.VerificacaoUltimaIntegracao>();

        services.AddScoped<IUnitOfWork, UnitOfWorkEfCore>();

        // Só usado em Development (o host decide): recria a base/tabelas do catálogo apagadas com a conta migradora.
        services.AddSingleton<Persistencia.Catalogo.GarantiaCatalogoDesenvolvimento>();
        services.AddScoped<IRepositorioTenant, RepositorioTenant>();
        services.AddScoped<IConsultaContribuintesPlataforma, Catalogo.ConsultaContribuintesCatalogo>();

        // ---- Multi-tenancy (Prompt 03) ----
        services.Configure<OpcoesBancoTenant>(configuration.GetSection(OpcoesBancoTenant.Secao));

        // Cofre de segredos por ambiente (Prompt 06): KeyVault em Produção (fail-fast),
        // configuração em Dev/Test. Nenhum segredo em código/appsettings (regra 7).
        services.AddSegredos(configuration, producao);
        services.AddSingleton<ConstrutorConexaoTenant>();

        // Leitura/autorização de tenant contra o catálogo (deny-by-default).
        services.AddScoped<ITenantStore, TenantStoreCatalogo>();
        services.AddScoped<IAutorizacaoTenant, AutorizacaoTenantCatalogo>();
        services.AddScoped<IConsultaAcessosUsuario, ConsultaAcessosUsuarioCatalogo>();

        // Fábrica fail-closed do contexto do tenant + provisionamento (DDL/grants/lock/migrations).
        services.AddSingleton<IFabricaContextoTenant, FabricaContextoTenant>();
        services.AddSingleton<IAdministradorBancoTenant, AdministradorBancoMySql>();
        services.AddSingleton<IMigradorTenant, MigradorTenant>();
        services.AddSingleton<IVersaoSchemaTenant, VersaoSchemaTenantAssembly>();
        services.AddSingleton<IValidadorProvisionamento, ValidadorProvisionamentoTenant>();
        services.AddSingleton<IMetricasProvisionamento, MetricasProvisionamento>();
        services.AddSingleton<ILockProvisionamento, LockProvisionamentoMySql>();

        // ---- Identidade / autorização (Prompt 04) ----
        services.Configure<OpcoesSegurancaIdentidade>(configuration.GetSection(OpcoesSegurancaIdentidade.Secao));

        // Primitivas de segurança (sem estado mutável compartilhado → singletons seguros).
        services.AddSingleton<IHasherSenha, HasherSenhaIdentity>();
        services.AddSingleton<IProvedorTotp, ProvedorTotpRfc6238>();
        services.AddSingleton<IProtetorSegredoMfa, ProtetorSegredoMfaAesGcm>();
        services.AddSingleton<IHashDeterministico, HashDeterministicoHmac>();
        services.AddSingleton<IGeradorTokens, GeradorTokens>();
        // Anti-replay de TOTP (Prompt 68): singleton com estado por-usuário (maior passo consumido) — por isso é
        // singleton (compartilhado pelo processo), thread-safe. Impede reapresentação do mesmo código no step-up.
        services.AddSingleton<IRegistroConsumoTotp, RegistroConsumoTotpMemoria>();

        // Política de LOCKOUT por usuário (Prompt 68, H2) — agora CONFIGURÁVEL via 'Seguranca:Lockout', com
        // padrão idêntico ao conservador (5 tentativas / 15 min). Sobrepõe o default registrado pela Application
        // (esta composição roda depois de AddApplication). Fail-fast: valores <= 0 abortam o startup, nunca
        // relaxam a proteção em silêncio.
        services.AddSingleton(LerPoliticaAutenticacao(configuration));

        // Repositórios de identidade/acesso (mesma unidade de trabalho do catálogo).
        services.AddScoped<IRepositorioUsuario, RepositorioUsuario>();
        services.AddScoped<IRepositorioAcesso, RepositorioAcesso>();
        services.AddScoped<Application.Identidade.IRepositorioGestaoUsuarios, RepositorioGestaoUsuarios>();

        // Consulta administrativa de USUÁRIOS por tenant (Prompt 72) — read-model sobre o catálogo.
        services.AddScoped<Application.Administracao.IConsultaUsuariosAdmin, Administracao.ConsultaUsuariosAdmin>();

        // ---- Segredos por tenant (Prompt 06) ----
        services.AddScoped<IAcessoSegredoTenant, AcessoSegredoTenant>();

        // ---- Ingestão de arquivos (Prompt 08) ----
        services.Configure<OpcoesUpload>(configuration.GetSection(OpcoesUpload.Secao));
        services.Configure<OpcoesArmazenamento>(configuration.GetSection(OpcoesArmazenamento.Secao));

        // Fail-closed: em Produção, a área privada de uploads (fora de wwwroot) é obrigatória.
        var diretorioRaizUploads = configuration.GetSection(OpcoesArmazenamento.Secao)["DiretorioRaiz"];
        if (producao && string.IsNullOrWhiteSpace(diretorioRaizUploads))
        {
            throw new InvalidOperationException(
                $"{OpcoesArmazenamento.Secao}:DiretorioRaiz é obrigatório em Produção (área privada de uploads).");
        }

        // A camada Application recebe as opções como POCO (não referencia o pacote de Options).
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OpcoesUpload>>().Value);

        // Storage privado + área temporária + scanner (fail-closed) + validador de conteúdo.
        services.AddSingleton<IArmazenamentoArquivos, ArmazenamentoArquivosFilesystem>();
        services.AddSingleton<IAreaTrabalhoTemporaria, AreaTrabalhoTemporaria>();
        // Antivírus: Windows Defender (MpCmdRun) no servidor Windows — ou quando configurado —, senão nenhum motor. Sem
        // motor ou com falha, o upload é rejeitado enquanto Upload:AntivirusObrigatorio=true (fail-closed). Motor
        // inválido falha a inicialização (nunca vira "sem antivírus" em silêncio).
        services.Configure<OpcoesAntivirus>(configuration.GetSection(OpcoesAntivirus.Secao));
        var motorAntivirus = OpcoesAntivirus.InterpretarMotor(configuration.GetSection(OpcoesAntivirus.Secao)[nameof(OpcoesAntivirus.Motor)]);
        if (OpcoesAntivirus.UsarWindowsDefender(motorAntivirus, OperatingSystem.IsWindows()))
        {
            services.AddSingleton<IExecutorProcessoExterno, ExecutorProcessoExterno>();
            services.AddSingleton<IScannerAntivirus, ScannerAntivirusWindowsDefender>();
        }
        else
        {
            services.AddSingleton<IScannerAntivirus, ScannerAntivirusIndisponivel>();
        }

        services.AddSingleton<IValidadorConteudoArquivo, ValidadorConteudoArquivo>();

        // Metadados no banco do TENANT (obtido apenas pela fábrica fail-closed).
        services.AddScoped<IRepositorioArquivoImportacao, RepositorioArquivoImportacao>();

        // ---- Backup / Restore / Disaster Recovery (Prompt 50) ----
        // Política dirigida por configuração (retenção/paralelismo/cifra) e store em failure domain separado.
        services.Configure<Application.Backup.OpcoesBackup>(configuration.GetSection(Application.Backup.OpcoesBackup.Secao));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<Application.Backup.OpcoesBackup>>().Value);
        services.Configure<OpcoesArmazenamentoBackup>(configuration.GetSection(OpcoesArmazenamentoBackup.Secao));

        // Cifra do artefato (AES-256-GCM, chave do cofre — nunca junto ao backup) + store de artefatos.
        services.AddSingleton<Application.Backup.ICriptografiaBackup, Backup.CriptografiaBackupAesGcm>();
        services.AddSingleton<Application.Backup.IArmazenamentoBackup, Backup.ArmazenamentoBackupFilesystem>();

        // Cópia externa (offsite) conferida pelo hash e pacote dos arquivos do contribuinte (auditoria de 08/10/2026).
        services.AddSingleton<Application.Backup.ICopiaExternaBackup, Backup.CopiaExternaBackupFilesystem>();
        services.AddSingleton<Application.Backup.IEmpacotadorArquivosTenant, Backup.EmpacotadorArquivosTenantZip>();

        // Executor FÍSICO (mysqldump/mysql). Fail-closed por padrão: só liga o executor real quando
        // "Backup:ExecutorMySql:Habilitado=true" (host com MySQL + ferramentas). Sem isso ⇒ indisponível
        // (BLOCKED). Credenciais NUNCA na linha de comando: senha do cofre via arquivo de opções 0600 (§37/§38).
        services.Configure<OpcoesExecutorBackupMySql>(configuration.GetSection(OpcoesExecutorBackupMySql.Secao));
        var executorMySqlHabilitado = configuration.GetSection(OpcoesExecutorBackupMySql.Secao)
            .GetValue<bool>(nameof(OpcoesExecutorBackupMySql.Habilitado));
        if (executorMySqlHabilitado)
        {
            services.AddSingleton<Application.Backup.IExecutorBackupBanco, Backup.ExecutorBackupBancoMySql>();
        }
        else
        {
            services.AddSingleton<Application.Backup.IExecutorBackupBanco, Backup.ExecutorBackupIndisponivel>();
        }

        services.AddSingleton<Application.Backup.PoliticaRetencaoBackup>();

        // Alvos/detector/repositório do catálogo de backups (banco de CATÁLOGO).
        services.AddScoped<Application.Backup.IProvedorAlvosBackup, Backup.ProvedorAlvosBackupCatalogo>();
        services.AddScoped<Application.Backup.IDetectorMigracaoEmAndamento, Backup.DetectorMigracaoTenantCatalogo>();
        services.AddScoped<Application.Backup.IRepositorioRegistroBackup, Backup.RepositorioRegistroBackup>();

        // Serviços orquestradores (autorização de PLATAFORMA + MFA; isolamento A→A; nunca cross-tenant).
        services.AddScoped<Application.Backup.ServicoBackup>();
        services.AddScoped<Application.Backup.ServicoRestauracao>();
        // Backup AGENDADO (host do Worker; desligado por padrão — Backup:AgendamentoHabilitado).
        services.AddScoped<Application.Backup.ExecucaoBackupAgendado>();
        services.AddScoped<Application.Backup.ConsultaBackupsAutorizada>();
        // Backup sob demanda e restauração pela tela da plataforma (troca atômica de tabelas; base anterior preservada).
        services.AddScoped<Application.Backup.IOperacoesBaseRestauracao, Backup.OperacoesBaseRestauracaoMySql>();
        services.AddScoped<Application.Backup.BackupSobDemanda>();
        services.AddScoped<Application.Backup.RestauracaoPlataforma>();

        // Marca visual (logo configurável): GLOBAL no catálogo (admin de plataforma) + POR TENANT no
        // banco do tenant (admin do tenant). Leitura pública/serviço; alteração autorizada e auditada.
        services.AddScoped<Application.Marca.IRepositorioMarcaPlataforma, Marca.RepositorioMarcaPlataformaCatalogo>();
        services.AddScoped<Application.Marca.IRepositorioMarcaTenant, Marca.RepositorioMarcaTenant>();
        services.AddSingleton<Application.Marca.CacheResumoMarca>(); // UI-005: metadados da logo, por processo
        services.AddScoped<Application.Marca.ServicoMarcaPlataforma>();
        services.AddScoped<Application.Marca.ServicoMarcaTenant>();

        // Autorização de PLATAFORMA (deny-by-default; NUNCA tenant-escopada) para a administração do SaaS.
        services.Configure<OpcoesAutorizacaoPlataforma>(configuration.GetSection(OpcoesAutorizacaoPlataforma.Secao));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OpcoesAutorizacaoPlataforma>>().Value);
        services.AddScoped<IAutorizacaoPlataforma, Seguranca.AutorizacaoPlataforma>();

        // ---- Read-models (somente leitura) ----
        services.AddScoped<Application.Ingestao.IConsultaArquivosImportacao, Ingestao.ConsultaArquivosImportacaoTenant>();
        // Incidentes: read-model sobre o CATÁLOGO (governança de plataforma, cross-tenant).
        services.AddScoped<Application.Incidentes.IConsultaIncidentes, Incidentes.ConsultaIncidentesCatalogoQuery>();

        // Tarefas em segundo plano (ARQ-006): fila durável no catálogo; a tela enfileira e acompanha. O executor (e os
        // manipuladores) são registrados pelo host que executa (a Web: AddExecutorTarefas).
        services.Configure<Application.Tarefas.OpcoesTarefas>(configuration.GetSection(Application.Tarefas.OpcoesTarefas.Secao));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<Application.Tarefas.OpcoesTarefas>>().Value);
        services.AddSingleton<Application.Tarefas.SinalTarefas>();
        services.AddScoped<Application.Tarefas.IRepositorioTarefas, Tarefas.RepositorioTarefasCatalogo>();
        services.AddScoped<Application.Tarefas.FilaTarefasSegundoPlano>();

        // Acrescente aqui os registros do domínio do seu portal (repositórios no banco do tenant, serviços, read-models).

        // O PortalBaseTenantDbContext NÃO é registrado como serviço: só a fábrica
        // fail-closed o cria, e apenas a partir de um TenantContext autorizado.
        return services;
    }

    /// <summary>
    /// Lê a política de LOCKOUT de 'Seguranca:Lockout' (Prompt 68, H2). Padrão conservador quando ausente
    /// (5 tentativas / 15 min). Fail-fast: valores &lt;= 0 abortam o startup (nunca relaxam a proteção).
    /// </summary>
    private static PoliticaAutenticacao LerPoliticaAutenticacao(IConfiguration configuration)
    {
        var secao = configuration.GetSection("Seguranca:Lockout");
        var maxTentativas = int.TryParse(secao["MaxTentativas"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mt)
            ? mt
            : PoliticaAutenticacao.Padrao.MaxTentativas;
        var minutos = int.TryParse(secao["DuracaoBloqueioMinutos"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var db)
            ? db
            : (int)PoliticaAutenticacao.Padrao.DuracaoBloqueio.TotalMinutes;

        if (maxTentativas < 1 || minutos < 1)
        {
            throw new InvalidOperationException(
                "Configuração 'Seguranca:Lockout' inválida: MaxTentativas e DuracaoBloqueioMinutos precisam ser >= 1.");
        }

        return new PoliticaAutenticacao(maxTentativas, TimeSpan.FromMinutes(minutos));
    }
}
