using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PortalBase.Domain.Backup;

namespace PortalBase.Infrastructure.Persistencia.Catalogo.Configuracoes;

// CATÁLOGO DE BACKUPS (Prompt 50, Fase 13) — no banco de CATÁLOGO. Guarda SOMENTE metadados auditáveis
// (identidade, tipo, tenant, hash, tamanho, versão, local LÓGICO, retenção, teste de restauração).
// NUNCA guarda segredo/chave/senha/token nem connection string (regra 7) — apenas a REFERÊNCIA da chave.

internal sealed class ConfiguracaoRegistroBackup : IEntityTypeConfiguration<RegistroBackup>
{
    public void Configure(EntityTypeBuilder<RegistroBackup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("registro_backup");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Tipo).HasConversion<int>().IsRequired();
        builder.Property(x => x.TenantId);
        builder.Property(x => x.AlvoLogico).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.IniciadoEmUtc).IsRequired();
        builder.Property(x => x.ConcluidoEmUtc);
        builder.Property(x => x.AtualizadoEmUtc).IsRequired();
        builder.Property(x => x.TamanhoBytes).IsRequired();
        builder.Property(x => x.HashSha256).HasMaxLength(64);
        builder.Property(x => x.AlgoritmoHash).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Versao).HasMaxLength(200);
        builder.Property(x => x.LocalArmazenamentoLogico).HasMaxLength(256);
        builder.Property(x => x.Criptografado).IsRequired();
        builder.Property(x => x.ChaveRef).HasMaxLength(200); // NOME da chave no cofre, nunca o valor
        builder.Property(x => x.Imutavel).IsRequired();
        builder.Property(x => x.RetencaoAteUtc);
        builder.Property(x => x.RestoreTestado).IsRequired();
        builder.Property(x => x.RestoreTestadoEmUtc);
        builder.Property(x => x.UltimoErro).HasMaxLength(512);
        builder.Property(x => x.CopiaExterna).HasConversion<int?>();
        builder.Property(x => x.CopiaExternaEmUtc);
        builder.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.Tipo, x.Status }).HasDatabaseName("ix_backup_tenant_tipo_status");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_backup_status");
        builder.HasIndex(x => x.RetencaoAteUtc).HasDatabaseName("ix_backup_retencao");
        // Janela do backup agendado (tipo + início) e lista dos recentes (ordem por início).
        builder.HasIndex(x => new { x.Tipo, x.IniciadoEmUtc }).HasDatabaseName("ix_backup_tipo_iniciado");
    }
}
