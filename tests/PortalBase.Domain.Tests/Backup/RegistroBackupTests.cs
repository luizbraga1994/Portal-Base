using FluentAssertions;
using PortalBase.Domain.Backup;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Tests.Backup;

/// <summary>
/// Prompt 50 — invariantes do CATÁLOGO DE BACKUPS (Fase 13). Garante a separação CATALOG/TENANT no
/// próprio registro, a integridade (hash SHA-256 obrigatório na conclusão), o BACKUP ≠ RESTORE
/// (candidato só quando concluído com hash) e que segredo/chave nunca cabem no agregado (só referência).
/// </summary>
public sealed class RegistroBackupTests
{
    private const string HashValido = "0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Agora = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact] // backup de catálogo NÃO pode ter tenant
    public void Iniciar_Catalogo_Com_Tenant_Rejeitado()
    {
        var acao = () => RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, Guid.NewGuid(), "portalbase_catalogo", Agora);
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact] // backup de tenant EXIGE tenant válido
    public void Iniciar_Tenant_Sem_Tenant_Rejeitado()
    {
        var semTenant = () => RegistroBackup.Iniciar(TipoBackup.TenantCompleto, null, "tenant_x", Agora);
        var tenantVazio = () => RegistroBackup.Iniciar(TipoBackup.TenantCompleto, Guid.Empty, "tenant_x", Agora);
        semTenant.Should().Throw<EntradaInvalidaException>();
        tenantVazio.Should().Throw<EntradaInvalidaException>();
    }

    [Fact] // iniciado ainda não é restaurável (BACKUP ≠ RESTORE)
    public void Iniciar_Valido_NaoRestauravel()
    {
        var r = RegistroBackup.Iniciar(TipoBackup.TenantCompleto, Guid.NewGuid(), "tenant_x", Agora);
        r.Status.Should().Be(StatusBackup.Iniciado);
        r.CandidatoRestauracao.Should().BeFalse();
        r.AlgoritmoHash.Should().Be("SHA-256");
    }

    [Fact] // concluir exige hash SHA-256 válido; o hash é normalizado para minúsculo
    public void Concluir_Valido_Concluido_E_Restauravel()
    {
        var r = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "portalbase_catalogo", Agora);
        r.Concluir(HashValido, 2048, "v2.0", "primario/cat.bak.enc", criptografado: true, chaveRef: "cofre-key", imutavel: false, Agora.AddDays(7), Agora);

        r.Status.Should().Be(StatusBackup.Concluido);
        r.CandidatoRestauracao.Should().BeTrue();
        r.HashSha256.Should().Be(HashValido.ToLowerInvariant());
        r.ChaveRef.Should().Be("cofre-key"); // referência, nunca o valor
    }

    [Fact] // hash inválido ⇒ recusa (não confiar em backup sem integridade)
    public void Concluir_Hash_Invalido_Rejeitado()
    {
        var r = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "cat", Agora);
        var acao = () => r.Concluir("nao-e-hash", 10, "v", "l", false, null, false, Agora.AddDays(1), Agora);
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact] // criptografado sem referência de chave ⇒ recusa
    public void Concluir_Criptografado_Sem_ChaveRef_Rejeitado()
    {
        var r = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "cat", Agora);
        var acao = () => r.Concluir(HashValido, 10, "v", "l", criptografado: true, chaveRef: " ", imutavel: false, Agora.AddDays(1), Agora);
        acao.Should().Throw<EntradaInvalidaException>();
    }

    [Fact] // concluir só a partir de iniciado
    public void Concluir_Duas_Vezes_Rejeitado()
    {
        var r = RegistroBackup.Iniciar(TipoBackup.CatalogoCompleto, null, "cat", Agora);
        r.Concluir(HashValido, 10, "v", "l", false, null, false, Agora.AddDays(1), Agora);
        var acao = () => r.Concluir(HashValido, 10, "v", "l", false, null, false, Agora.AddDays(1), Agora);
        acao.Should().Throw<EstadoInvalidoException>();
    }

    [Fact] // teste de restauração só em backup concluído; registra o resultado
    public void RegistrarTesteRestauracao_Fluxo()
    {
        var r = RegistroBackup.Iniciar(TipoBackup.TenantCompleto, Guid.NewGuid(), "tenant_x", Agora);
        var antesConcluir = () => r.RegistrarTesteRestauracao(true, Agora);
        antesConcluir.Should().Throw<EstadoInvalidoException>();

        r.Concluir(HashValido, 10, "v", "l", false, null, false, Agora.AddDays(1), Agora);
        r.RegistrarTesteRestauracao(true, Agora.AddHours(1));
        r.RestoreTestado.Should().BeTrue();
        r.RestoreTestadoEmUtc.Should().Be(Agora.AddHours(1));
    }

    [Fact] // corrompido não é mais restaurável
    public void MarcarCorrompido_Bloqueia_Restauracao()
    {
        var r = RegistroBackup.Iniciar(TipoBackup.TenantCompleto, Guid.NewGuid(), "tenant_x", Agora);
        r.Concluir(HashValido, 10, "v", "l", false, null, false, Agora.AddDays(1), Agora);
        r.MarcarCorrompido("hash divergente", Agora);
        r.Status.Should().Be(StatusBackup.Corrompido);
        r.CandidatoRestauracao.Should().BeFalse();
    }

    [Fact]
    public void DentroDaRetencao_Avalia_A_Janela()
    {
        var r = RegistroBackup.Iniciar(TipoBackup.TenantCompleto, Guid.NewGuid(), "tenant_x", Agora);
        r.Concluir(HashValido, 10, "v", "l", false, null, false, Agora.AddDays(5), Agora);
        r.DentroDaRetencao(Agora.AddDays(3)).Should().BeTrue();
        r.DentroDaRetencao(Agora.AddDays(6)).Should().BeFalse();
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("short", false)]
    [InlineData(null, false)]
    public void HashValido_Reconhece_Formato(string? hash, bool esperado)
        => RegistroBackup.HashValido(hash).Should().Be(esperado);
}
