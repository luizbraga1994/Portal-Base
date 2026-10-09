using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Infrastructure.Persistencia.Catalogo.Configuracoes;

internal sealed class ConfiguracaoTenant : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tenant");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Cnpj)
            .HasConversion(cnpj => cnpj.Valor, valor => Cnpj.Criar(valor))
            .HasColumnName("cnpj")
            .HasMaxLength(14)
            .IsRequired();
        builder.HasIndex(t => t.Cnpj).IsUnique().HasDatabaseName("ux_tenant_cnpj");

        // Nome escolhido (MAIÚSCULO) — único: define a base física portalbase_tenant_<nome>.
        builder.Property(t => t.Nome).HasColumnName("nome").HasMaxLength(NomeTenant.TamanhoMaximo).IsRequired();
        builder.HasIndex(t => t.Nome).IsUnique().HasDatabaseName("ux_tenant_nome");

        builder.Property(t => t.RazaoSocial).HasMaxLength(200).IsRequired();

        builder.Property(t => t.NomeBaseFisica).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.NomeBaseFisica).IsUnique().HasDatabaseName("ux_tenant_base_fisica");

        builder.Property(t => t.ReferenciaSegredoConexao).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Estado).HasConversion<int>().IsRequired();
        builder.HasIndex(t => t.Estado).HasDatabaseName("ix_tenant_estado");
        builder.Property(t => t.SchemaVersion).HasMaxLength(200);
        builder.Property(t => t.UltimaMigracao).HasMaxLength(200);
        builder.Property(t => t.UltimoErro).HasMaxLength(512);
        builder.Property(t => t.CriadoEmUtc).IsRequired();
        builder.Property(t => t.AtualizadoEmUtc).IsRequired();
        builder.Property(t => t.ConcurrencyStamp).IsConcurrencyToken().IsRequired();
    }
}

internal sealed class ConfiguracaoUsuario : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("usuario");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.Login).HasMaxLength(128).IsRequired();
        builder.HasIndex(u => u.Login).IsUnique().HasDatabaseName("ux_usuario_login");

        builder.Property(u => u.Nome).HasMaxLength(200).IsRequired();

        builder.Property(u => u.Email).HasMaxLength(256).IsRequired();
        builder.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ux_usuario_email");

        builder.Property(u => u.Status).HasConversion<int>().IsRequired();

        // Credencial e MFA: apenas hash/segredo protegido — NUNCA senha ou segredo em claro.
        builder.Property(u => u.HashSenha).HasColumnName("hash_senha").HasMaxLength(512);
        builder.Property(u => u.SegredoMfaProtegido).HasColumnName("segredo_mfa_protegido").HasMaxLength(1024);
        builder.Property(u => u.SegredoMfaPendenteProtegido).HasColumnName("segredo_mfa_pendente_protegido").HasMaxLength(1024);
        builder.Property(u => u.MfaHabilitado).HasColumnName("mfa_habilitado").IsRequired();
        builder.Property(u => u.MfaConfirmadoEmUtc).HasColumnName("mfa_confirmado_utc");
        builder.Property(u => u.AcessosFalhosConsecutivos).HasColumnName("acessos_falhos").IsRequired();
        builder.Property(u => u.BloqueadoAteUtc).HasColumnName("bloqueado_ate_utc");
        builder.Property(u => u.UltimoAcessoUtc).HasColumnName("ultimo_acesso_utc");
        builder.Property(u => u.SecurityStamp).HasColumnName("security_stamp").IsRequired();
        builder.Property(u => u.AdministradorPlataforma).HasColumnName("administrador_plataforma").HasDefaultValue(false).IsRequired();
        builder.Property(u => u.MfaObrigatorio).HasColumnName("mfa_obrigatorio").HasDefaultValue(false).IsRequired();
        builder.Property(u => u.TrocarSenhaNoProximoAcesso).HasColumnName("trocar_senha_proximo_acesso").HasDefaultValue(false).IsRequired();
        builder.Ignore(u => u.CadastroMfaPendente);

        builder.Property(u => u.CriadoEmUtc).IsRequired();
        builder.Property(u => u.AtualizadoEmUtc).IsRequired();
        builder.Property(u => u.ConcurrencyStamp).IsConcurrencyToken().IsRequired();
    }
}

internal sealed class ConfiguracaoTokenRecuperacaoConta : IEntityTypeConfiguration<TokenRecuperacaoConta>
{
    public void Configure(EntityTypeBuilder<TokenRecuperacaoConta> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("token_recuperacao");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.UsuarioId).IsRequired();

        // Somente o HASH do token é persistido (seção 27). Índice para busca por igualdade.
        builder.Property(t => t.HashToken).HasColumnName("hash_token").HasMaxLength(512).IsRequired();
        builder.HasIndex(t => t.HashToken).IsUnique().HasDatabaseName("ux_token_recuperacao_hash");

        builder.Property(t => t.CriadoEmUtc).IsRequired();
        builder.Property(t => t.ExpiraEmUtc).IsRequired();
        builder.Property(t => t.UsadoEmUtc);

        builder.HasOne<Usuario>().WithMany().HasForeignKey(t => t.UsuarioId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConfiguracaoCodigoRecuperacaoMfa : IEntityTypeConfiguration<CodigoRecuperacaoMfa>
{
    public void Configure(EntityTypeBuilder<CodigoRecuperacaoMfa> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("codigo_recuperacao_mfa");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.UsuarioId).IsRequired();

        // Somente o HASH do código é persistido (seção 36).
        builder.Property(c => c.HashCodigo).HasColumnName("hash_codigo").HasMaxLength(512).IsRequired();
        builder.HasIndex(c => new { c.UsuarioId, c.HashCodigo }).IsUnique().HasDatabaseName("ux_codigo_mfa_usuario_hash");

        builder.Property(c => c.CriadoEmUtc).IsRequired();
        builder.Property(c => c.UsadoEmUtc);

        builder.HasOne<Usuario>().WithMany().HasForeignKey(c => c.UsuarioId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConfiguracaoPapel : IEntityTypeConfiguration<Papel>
{
    public void Configure(EntityTypeBuilder<Papel> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("papel");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Nome).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Descricao).HasMaxLength(256).IsRequired();

        // Perfis personalizados pertencem a um contribuinte (TenantId); os do sistema têm TenantId nulo. Nome único por
        // contribuinte (o caso de uso também impede repetir o nome de um papel do sistema).
        builder.Property(p => p.TenantId).HasColumnName("tenant_id");
        builder.HasIndex(p => new { p.TenantId, p.Nome }).IsUnique().HasDatabaseName("ux_papel_tenant_nome");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.Ignore(p => p.EhDoSistema);

        foreach (var papel in Enum.GetValues<PapelConhecido>())
        {
            builder.HasData(new
            {
                Id = SeedCatalogo.PapelId(papel),
                Nome = papel.ToString(),
                Descricao = Descricao(papel),
            });
        }
    }

    private static string Descricao(PapelConhecido papel) => papel switch
    {
        PapelConhecido.Leitor => "Acesso somente leitura.",
        PapelConhecido.Analista => "Cadastro, edição e importação de dados.",
        PapelConhecido.Administrador => "Administração completa do tenant.",
        _ => papel.ToString(),
    };
}

internal sealed class ConfiguracaoPermissao : IEntityTypeConfiguration<Permissao>
{
    public void Configure(EntityTypeBuilder<Permissao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("permissao");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Chave).HasMaxLength(64).IsRequired();
        builder.HasIndex(p => p.Chave).IsUnique().HasDatabaseName("ux_permissao_chave");
        builder.Property(p => p.Descricao).HasMaxLength(256).IsRequired();

        foreach (var permissao in Enum.GetValues<PermissaoConhecida>())
        {
            builder.HasData(new
            {
                Id = SeedCatalogo.PermissaoId(permissao),
                Chave = SeedCatalogo.ChavePermissao(permissao),
                Descricao = permissao.ToString(),
            });
        }
    }
}

internal sealed class ConfiguracaoPapelPermissao : IEntityTypeConfiguration<PapelPermissao>
{
    public void Configure(EntityTypeBuilder<PapelPermissao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("papel_permissao");
        builder.HasKey(pp => new { pp.PapelId, pp.PermissaoId });

        builder.HasOne<Papel>().WithMany().HasForeignKey(pp => pp.PapelId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Permissao>().WithMany().HasForeignKey(pp => pp.PermissaoId).OnDelete(DeleteBehavior.Cascade);

        foreach (var (papel, permissao) in SeedCatalogo.MapaPapelPermissao)
        {
            builder.HasData(new
            {
                PapelId = SeedCatalogo.PapelId(papel),
                PermissaoId = SeedCatalogo.PermissaoId(permissao),
            });
        }
    }
}

internal sealed class ConfiguracaoUsuarioTenant : IEntityTypeConfiguration<UsuarioTenant>
{
    public void Configure(EntityTypeBuilder<UsuarioTenant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("usuario_tenant");
        builder.HasKey(ut => ut.Id);
        builder.Property(ut => ut.Id).ValueGeneratedNever();
        builder.Property(ut => ut.Status).HasConversion<int>().IsRequired();
        builder.Property(ut => ut.CriadoEmUtc).IsRequired();
        builder.Property(ut => ut.ConcurrencyStamp).IsConcurrencyToken().IsRequired();

        builder.HasIndex(ut => new { ut.UsuarioId, ut.TenantId }).IsUnique().HasDatabaseName("ux_usuario_tenant");

        builder.HasOne<Usuario>().WithMany().HasForeignKey(ut => ut.UsuarioId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(ut => ut.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConfiguracaoUsuarioTenantPapel : IEntityTypeConfiguration<UsuarioTenantPapel>
{
    public void Configure(EntityTypeBuilder<UsuarioTenantPapel> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("usuario_tenant_papel");
        builder.HasKey(utp => new { utp.UsuarioTenantId, utp.PapelId });

        builder.HasOne<UsuarioTenant>().WithMany().HasForeignKey(utp => utp.UsuarioTenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Papel>().WithMany().HasForeignKey(utp => utp.PapelId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ConfiguracaoRegistroAuditoria : IEntityTypeConfiguration<RegistroAuditoria>
{
    public void Configure(EntityTypeBuilder<RegistroAuditoria> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("registro_auditoria");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.PrincipalId).HasMaxLength(256).IsRequired();
        builder.Property(r => r.Tenant).HasMaxLength(64).IsRequired();
        builder.Property(r => r.TipoAtor).IsRequired();
        builder.Property(r => r.Operacao).HasColumnName("acao").HasMaxLength(64).IsRequired();
        builder.Property(r => r.TipoRecurso).HasMaxLength(64);
        builder.Property(r => r.RecursoId).HasMaxLength(128);
        builder.Property(r => r.Resultado).HasMaxLength(16).IsRequired();
        builder.Property(r => r.Motivo).HasMaxLength(512);
        builder.Property(r => r.CorrelationId).HasMaxLength(64);
        builder.Property(r => r.TraceId).HasMaxLength(64);
        builder.Property(r => r.IpAddress).HasMaxLength(64);
        builder.Property(r => r.UserAgent).HasMaxLength(256);
        builder.Property(r => r.Origem).HasMaxLength(32);
        builder.Property(r => r.Job).HasMaxLength(128);
        builder.Property(r => r.Competencia).HasMaxLength(7);
        builder.Property(r => r.Protocolo).HasMaxLength(64);
        builder.Property(r => r.Categoria).IsRequired();
        builder.Property(r => r.ValoresAnteriores).HasColumnType("text");
        builder.Property(r => r.ValoresNovos).HasColumnType("text");
        builder.Property(r => r.Metadados).HasColumnType("text");
        builder.Property(r => r.OcorridoEmUtc).IsRequired();
        builder.Property(r => r.IntegridadeHash).HasColumnName("integridade_hash").HasMaxLength(64).IsRequired();

        // Índices para as consultas comuns (seção 15) — sem índice em payload grande (JSON/text).
        builder.HasIndex(r => new { r.Tenant, r.OcorridoEmUtc }).HasDatabaseName("ix_auditoria_tenant_data");
        builder.HasIndex(r => new { r.PrincipalId, r.OcorridoEmUtc }).HasDatabaseName("ix_auditoria_ator_data");
        builder.HasIndex(r => new { r.Operacao, r.OcorridoEmUtc }).HasDatabaseName("ix_auditoria_acao_data");
        builder.HasIndex(r => r.CorrelationId).HasDatabaseName("ix_auditoria_correlation");
        builder.HasIndex(r => r.TraceId).HasDatabaseName("ix_auditoria_trace");
        builder.HasIndex(r => new { r.TipoRecurso, r.RecursoId }).HasDatabaseName("ix_auditoria_recurso");
        builder.HasIndex(r => new { r.Tenant, r.Protocolo }).HasDatabaseName("ix_auditoria_protocolo");
        builder.HasIndex(r => new { r.Tenant, r.Competencia }).HasDatabaseName("ix_auditoria_competencia");
    }
}
