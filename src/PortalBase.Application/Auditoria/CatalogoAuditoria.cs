namespace PortalBase.Application.Auditoria;

/// <summary>
/// Tipo do ATOR de um evento de auditoria (seção 4). Diferencia origem humana de execução
/// automática — nunca atribuir uma operação de sistema a um usuário inexistente.
/// </summary>
public enum TipoAtor
{
    Usuario = 1,
    Sistema = 2,
    JobBackground = 3,
    Integracao = 4,
}

/// <summary>
/// Categoria de RETENÇÃO de um registro (seção 18). Cada categoria carrega o prazo legal
/// mínimo em anos; a política de retenção usa isto para identificar a janela obrigatória.
/// </summary>
public enum CategoriaRetencao
{
    /// <summary>Trilha de auditoria — 10 anos.</summary>
    Auditoria = 1,

    /// <summary>Arquivos originais de importação — 5 anos.</summary>
    ArquivosImportacao = 5,
}

/// <summary>
/// Catálogo CENTRALIZADO de ações de auditoria (seção 5). Nomes estáveis e pesquisáveis,
/// acrescente as ações do domínio do seu portal aqui. Evita strings soltas.
/// </summary>
public static class AcaoAuditoria
{
    // Identidade
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string AccountLocked = "AccountLocked";
    public const string Logout = "Logout";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordRecoveryStarted = "PasswordRecoveryStarted";
    public const string PasswordRecoveryCompleted = "PasswordRecoveryCompleted";
    public const string MFAEnabled = "MFAEnabled";
    public const string MFAConfirmed = "MFAConfirmed";
    public const string MFAValidated = "MFAValidated";
    public const string MFARecoveryUsed = "MFARecoveryUsed";
    public const string MFADisabled = "MFADisabled";
    public const string SessionRevoked = "SessionRevoked";

    // Autorização
    public const string AuthorizationDenied = "AuthorizationDenied";
    public const string AccessGranted = "AccessGranted";
    public const string AccessRevoked = "AccessRevoked";
    public const string RoleAssigned = "RoleAssigned";
    public const string RoleRemoved = "RoleRemoved";
    public const string PermissionChanged = "PermissionChanged";
    public const string PrivilegeEscalationAttempt = "PrivilegeEscalationAttempt";

    // Perfis personalizados do contribuinte (cadastro de perfis e das permissões de cada um).
    public const string RoleCreated = "RoleCreated";
    public const string RoleUpdated = "RoleUpdated";
    public const string RoleDeleted = "RoleDeleted";

    // Perfil global "Administrador da plataforma" (acesso total, todos os contribuintes, sem MFA).
    public const string PlatformAdminGranted = "PlatformAdminGranted";
    public const string PlatformAdminRevoked = "PlatformAdminRevoked";
    public const string TenantAccessDenied = "TenantAccessDenied";

    // Tenant
    public const string TenantContextSelected = "TenantContextSelected";
    public const string TenantContextSwitched = "TenantContextSwitched";
    public const string TenantAccessLost = "TenantAccessLost";
    public const string TenantDisabled = "TenantDisabled";
    public const string TenantProvisioningStarted = "TenantProvisioningStarted";
    public const string TenantProvisioningCompleted = "TenantProvisioningCompleted";
    public const string TenantProvisioningFailed = "TenantProvisioningFailed";
    public const string TenantDatabaseCreated = "TenantDatabaseCreated";
    public const string TenantDatabaseMigrationStarted = "TenantDatabaseMigrationStarted";
    public const string TenantDatabaseMigrationCompleted = "TenantDatabaseMigrationCompleted";
    public const string TenantDatabaseMigrationFailed = "TenantDatabaseMigrationFailed";

    /// <summary>Base já existente reaproveitada no provisionamento (vazia, do próprio tenant ou do mesmo CNPJ).</summary>
    public const string TenantDatabaseReused = "TenantDatabaseReused";

    /// <summary>Provisionamento recusado: a base com esse nome já existe com dados de OUTRO contribuinte (ou sem dono).</summary>
    public const string TenantDatabaseOwnershipConflict = "TenantDatabaseOwnershipConflict";

    // Ciclo de vida operacional do tenant (Prompt 09, seção 18).
    public const string TenantActivated = "TenantActivated";

    /// <summary>Dados cadastrais do contribuinte alterados pela tela (antes/depois só dos campos alterados).</summary>
    public const string TenantDetailsUpdated = "TenantDetailsUpdated";
    public const string TenantReactivated = "TenantReactivated";

    // Administração
    public const string UserCreated = "UserCreated";
    public const string UserUpdated = "UserUpdated";
    public const string UserDisabled = "UserDisabled";
    public const string UserEnabled = "UserEnabled";
    public const string TenantUserLinked = "TenantUserLinked";
    public const string TenantUserUnlinked = "TenantUserUnlinked";

    // Consulta da própria auditoria (seção 24 — auditar o acesso à auditoria, sem recursão)
    public const string AuditQueried = "AuditQueried";

    // Acesso ao cofre (Prompt 06, seção 20) — nunca registra o valor do segredo.
    public const string SecretAccessed = "SecretAccessed";
    public const string SecretAccessDenied = "SecretAccessDenied";

    /// <summary>Definição/rotação de um segredo do tenant no cofre — NUNCA registra o valor (só o nome lógico).</summary>
    public const string SecretUpdated = "SecretUpdated";

    /// <summary>Alteração da configuração de uma integração do tenant (ex.: uma API externa) — sem segredos.</summary>
    public const string IntegrationConfigUpdated = "IntegrationConfigUpdated";

    /// <summary>Teste ATIVO de conexão de uma integração do tenant (ex.: uma API externa) — registra só a categoria do desfecho.</summary>
    public const string IntegrationConnectionTested = "IntegrationConnectionTested";
    // Autorização de PLATAFORMA negada (permissão global sem a allowlist de administradores da plataforma).
    public const string PlataformaAcessoNegado = "PlataformaAcessoNegado";

    // Governança de incidentes de segurança (Prompt 30, seções 45-47) — nunca registra payload/PII bruta.
    public const string IncidenteRegistrado = "IncidenteRegistrado";
    public const string IncidenteAtualizado = "IncidenteAtualizado";
    public const string IncidenteClassificado = "IncidenteClassificado";
    public const string IncidenteEncerrado = "IncidenteEncerrado";

    // Operações de produção (Prompt 31, seções 50-52) — auditoria de ações administrativas críticas. Sem payload.
    public const string AlertaOperacionalDisparado = "AlertaOperacionalDisparado";
    public const string IntegracaoHabilitada = "IntegracaoHabilitada";
    public const string IntegracaoDesabilitada = "IntegracaoDesabilitada";
    public const string ConfiguracaoOperacionalAlterada = "ConfiguracaoOperacionalAlterada";
    public const string WorkerDesbloqueado = "WorkerDesbloqueado";
    public const string ThresholdOperacionalAlterado = "ThresholdOperacionalAlterado";
    public const string ManutencaoOperacionalExecutada = "ManutencaoOperacionalExecutada";

    // Backup / Restore / Disaster Recovery (Prompt 50, Fase 31) — operação de PLATAFORMA. Registra tenant,
    // backup id, tipo, hash e resultado. NUNCA registra segredo/chave/senha/token nem connection string.
    public const string BackupStarted = "BackupStarted";
    public const string BackupCompleted = "BackupCompleted";
    public const string BackupFailed = "BackupFailed";
    public const string BackupCorrupted = "BackupCorrupted";
    public const string BackupPruned = "BackupPruned";

    /// <summary>Artefato copiado para o destino externo (offsite) e conferido pelo hash (BKP-003).</summary>
    public const string BackupCopiedOffsite = "BackupCopiedOffsite";

    /// <summary>A cópia externa (offsite) do artefato falhou ou não conferiu; o backup existe só no destino primário.</summary>
    public const string BackupOffsiteFailed = "BackupOffsiteFailed";
    public const string RestoreStarted = "RestoreStarted";
    public const string RestoreCompleted = "RestoreCompleted";
    public const string RestoreFailed = "RestoreFailed";
    public const string RestoreCrossTenantDenied = "RestoreCrossTenantDenied";
    public const string RestoreTestCompleted = "RestoreTestCompleted";

    /// <summary>Backup restaurado SOBRE a base do contribuinte (troca atômica; a base anterior fica preservada).</summary>
    public const string RestoreAppliedToTenant = "RestoreAppliedToTenant";

    /// <summary>Falha ao aplicar a restauração na base do contribuinte (nada trocado ou schema não atualizado).</summary>
    public const string RestoreApplyFailed = "RestoreApplyFailed";

    // Ingestão de arquivos (Prompt 08, seção 28) — nunca registra conteúdo do arquivo.
    public const string UploadStarted = "UploadStarted";
    public const string UploadAccepted = "UploadAccepted";
    public const string UploadRejected = "UploadRejected";
    public const string UploadStored = "UploadStored";
    public const string UploadDuplicated = "UploadDuplicated";
    public const string UploadInfected = "UploadInfected";
    public const string UploadFailed = "UploadFailed";

    // Marca visual (logo) — atualização da identidade visual. Nunca registra o binário, apenas o hash.
    public const string MarcaPlataformaAtualizada = "MarcaPlataformaAtualizada";
    public const string MarcaTenantAtualizada = "MarcaTenantAtualizada";

    // Configuração do e-mail (SMTP) pela tela da plataforma — antes/depois sem segredo; teste de envio.
    public const string EmailConfigurationUpdated = "EmailConfigurationUpdated";

    // MFA e senha administrados no cadastro do usuário.
    public const string MfaRequirementChanged = "MfaRequirementChanged";
    public const string MfaRemovedByAdministrator = "MfaRemovedByAdministrator";
    public const string TemporaryPasswordIssued = "TemporaryPasswordIssued";
    public const string EmailTestSent = "EmailTestSent";
}
