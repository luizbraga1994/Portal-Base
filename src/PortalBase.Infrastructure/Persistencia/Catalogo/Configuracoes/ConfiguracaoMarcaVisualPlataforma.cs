using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PortalBase.Domain.Marca;

namespace PortalBase.Infrastructure.Persistencia.Catalogo.Configuracoes;

// MARCA VISUAL GLOBAL do portal (logo padrão) — no banco de CATÁLOGO, linha ÚNICA. Guarda apenas o
// binário da imagem (PNG/JPEG), seu tipo e hash de integridade. NUNCA guarda segredo (regra 7).
internal sealed class ConfiguracaoMarcaVisualPlataforma : IEntityTypeConfiguration<MarcaVisual>
{
    public void Configure(EntityTypeBuilder<MarcaVisual> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("marca_visual");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Conteudo).HasColumnType("mediumblob").IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.HashSha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TamanhoBytes).IsRequired();
        builder.Property(x => x.AtualizadoEmUtc).IsRequired();
        builder.Property(x => x.AtualizadoPorUsuarioId);
        builder.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().IsRequired();
    }
}
