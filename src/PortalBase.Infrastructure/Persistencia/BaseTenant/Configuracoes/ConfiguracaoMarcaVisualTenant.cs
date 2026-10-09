using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PortalBase.Domain.Marca;

namespace PortalBase.Infrastructure.Persistencia.BaseTenant.Configuracoes;

// MARCA VISUAL do CONTRIBUINTE (logo) — no banco do TENANT, linha ÚNICA. Isolada por definição (cada
// contribuinte tem seu próprio banco). Guarda só o binário da imagem, tipo e hash; nunca segredo (regra 7).
internal sealed class ConfiguracaoMarcaVisualTenant : IEntityTypeConfiguration<MarcaVisual>
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
