using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PortalBase.Domain.Arquivos;

namespace PortalBase.Infrastructure.Persistencia.BaseTenant.Configuracoes;

/// <summary>
/// Mapeamento dos METADADOS do arquivo de importação no banco do TENANT (Prompt 08, seções 18,
/// 19). O conteúdo NUNCA é persistido aqui. O índice ÚNICO sobre <c>Sha256</c> é a segunda
/// barreira de idempotência (tenant-scoped) sob concorrência (seção 17): o banco rejeita o
/// duplicado (erro 1062 → traduzido pelo repositório).
/// </summary>
internal sealed class ConfiguracaoArquivoImportacao : IEntityTypeConfiguration<ArquivoImportacao>
{
    public void Configure(EntityTypeBuilder<ArquivoImportacao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("arquivo_importacao");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.NomeOriginalSanitizado).HasMaxLength(260).IsRequired();
        builder.Property(x => x.NomeSeguro).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Extensao).HasMaxLength(16).IsRequired();
        builder.Property(x => x.ContentTypeDetectado).HasMaxLength(128).IsRequired();
        builder.Property(x => x.TamanhoBytes).IsRequired();

        builder.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.Sha256).IsUnique().HasDatabaseName("ux_arquivo_sha256");

        builder.Property(x => x.Estado).HasConversion<int>().IsRequired();
        builder.Property(x => x.ResultadoScan).HasConversion<int>().IsRequired();
        builder.Property(x => x.EscaneadoEmUtc);

        builder.Property(x => x.ReferenciaArmazenamento).HasMaxLength(128);
        builder.Property(x => x.UsuarioId).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CategoriaRetencao).IsRequired();
        builder.Property(x => x.ExpiraEmUtc).IsRequired();
        builder.Property(x => x.MotivoRejeicao).HasMaxLength(256);
        builder.Property(x => x.EnviadoEmUtc).IsRequired();
        builder.Property(x => x.AtualizadoEmUtc).IsRequired();

        // Token de concorrência otimista (regra 1: concorrência).
        builder.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().IsRequired();

        // Consulta comum: arquivos processáveis por estado.
        builder.HasIndex(x => x.Estado).HasDatabaseName("ix_arquivo_estado");
    }
}
