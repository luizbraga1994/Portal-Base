using FluentAssertions;
using PortalBase.Domain.Catalogo;
using PortalBase.Domain.Excecoes;

namespace PortalBase.Domain.Tests.Catalogo;

/// <summary>Estados de credencial, lockout, MFA e marca de segurança do <see cref="Usuario"/> (Prompt 04).</summary>
public sealed class UsuarioCredencialTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static Usuario Novo() => Usuario.Criar("joana", "Joana", "joana@exemplo.com", Momento);

    [Fact]
    public void Novo_Usuario_Nao_Possui_Credencial()
    {
        var usuario = Novo();
        usuario.PossuiCredencial.Should().BeFalse();
        usuario.MfaHabilitado.Should().BeFalse();
        usuario.SecurityStamp.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void DefinirCredencial_Rotaciona_SecurityStamp_E_Limpa_Lockout()
    {
        var usuario = Novo();
        var stampInicial = usuario.SecurityStamp;

        usuario.DefinirCredencial("hash-abc", Momento);

        usuario.PossuiCredencial.Should().BeTrue();
        usuario.HashSenha.Should().Be("hash-abc");
        usuario.SecurityStamp.Should().NotBe(stampInicial);
    }

    [Fact]
    public void Falhas_Consecutivas_Atingindo_Limite_Bloqueiam_Temporariamente()
    {
        var usuario = Novo();
        usuario.DefinirCredencial("h", Momento);

        usuario.RegistrarFalhaAutenticacao(3, TimeSpan.FromMinutes(15), Momento);
        usuario.RegistrarFalhaAutenticacao(3, TimeSpan.FromMinutes(15), Momento);
        usuario.EstaImpedidoDeAutenticar(Momento).Should().BeFalse();

        usuario.RegistrarFalhaAutenticacao(3, TimeSpan.FromMinutes(15), Momento); // 3ª falha → lockout
        usuario.EstaImpedidoDeAutenticar(Momento).Should().BeTrue();
        usuario.EstaImpedidoDeAutenticar(Momento.AddMinutes(16)).Should().BeFalse(); // janela expira
    }

    [Fact]
    public void Sucesso_Zera_Contador_E_Marca_Ultimo_Acesso()
    {
        var usuario = Novo();
        usuario.DefinirCredencial("h", Momento);
        usuario.RegistrarFalhaAutenticacao(5, TimeSpan.FromMinutes(15), Momento);

        usuario.RegistrarSucessoAutenticacao(Momento.AddMinutes(1));

        usuario.AcessosFalhosConsecutivos.Should().Be(0);
        usuario.UltimoAcessoUtc.Should().Be(Momento.AddMinutes(1));
    }

    [Fact]
    public void Confirmar_Mfa_Sem_Preparar_Falha()
    {
        var usuario = Novo();
        var acao = () => usuario.ConfirmarMfa(Momento);
        acao.Should().Throw<EstadoInvalidoException>();
    }

    [Fact]
    public void Mfa_Preparado_E_Confirmado_Fica_Habilitado_E_Rotaciona_Stamp()
    {
        var usuario = Novo();
        usuario.DefinirCredencial("h", Momento);
        var stampAntes = usuario.SecurityStamp;

        usuario.PrepararMfa("segredo-protegido", Momento);
        usuario.MfaHabilitado.Should().BeFalse(); // pendente até confirmar

        usuario.ConfirmarMfa(Momento);
        usuario.MfaHabilitado.Should().BeTrue();
        usuario.SecurityStamp.Should().NotBe(stampAntes);
    }

    [Fact]
    public void Desabilitar_Ou_Bloquear_Rotaciona_Stamp_Para_Revogar_Sessoes()
    {
        var usuario = Novo();
        var s0 = usuario.SecurityStamp;

        usuario.RevogarSessoes(Momento);
        var s1 = usuario.SecurityStamp;
        s1.Should().NotBe(s0);

        usuario.Bloquear(Momento);
        usuario.SecurityStamp.Should().NotBe(s1);
        usuario.EstaImpedidoDeAutenticar(Momento).Should().BeTrue();
    }

    [Fact]
    public void Senha_Temporaria_Exige_Troca_No_Proximo_Acesso_E_A_Senha_Nova_Limpa_A_Exigencia()
    {
        var usuario = Novo();
        var stamp = usuario.SecurityStamp;

        usuario.DefinirSenhaTemporaria("hash-temporario", Momento);

        usuario.PossuiCredencial.Should().BeTrue();
        usuario.TrocarSenhaNoProximoAcesso.Should().BeTrue();
        usuario.SecurityStamp.Should().NotBe(stamp, "as sessões abertas caem");

        usuario.DefinirCredencial("hash-definitivo", Momento.AddMinutes(5));

        usuario.TrocarSenhaNoProximoAcesso.Should().BeFalse();
        usuario.HashSenha.Should().Be("hash-definitivo");
    }

    [Fact]
    public void Mfa_Exigido_Fica_Pendente_Ate_O_Autenticador_Ser_Cadastrado()
    {
        var usuario = Novo();
        usuario.CadastroMfaPendente.Should().BeFalse("por padrão o MFA não é exigido");

        var stamp = usuario.SecurityStamp;
        var concorrencia = usuario.ConcurrencyStamp;
        usuario.DefinirMfaObrigatorio(true, Momento);

        usuario.MfaObrigatorio.Should().BeTrue();
        usuario.CadastroMfaPendente.Should().BeTrue();
        usuario.SecurityStamp.Should().Be(stamp, "exigir o MFA não derruba a sessão: vale no próximo login");
        usuario.ConcurrencyStamp.Should().NotBe(concorrencia);

        usuario.PrepararMfa("segredo-protegido", Momento);
        usuario.CadastroMfaPendente.Should().BeTrue("segredo gerado mas ainda não confirmado com um código");
        usuario.ConfirmarMfa(Momento);
        usuario.CadastroMfaPendente.Should().BeFalse();

        usuario.DesabilitarMfa(Momento);
        usuario.CadastroMfaPendente.Should().BeTrue("removido o autenticador, o próximo login volta ao cadastro");

        usuario.DefinirMfaObrigatorio(false, Momento);
        usuario.CadastroMfaPendente.Should().BeFalse();
    }

    [Fact]
    public void Definir_Mfa_Obrigatorio_Igual_Ao_Atual_Nao_Altera_Nada()
    {
        var usuario = Novo();
        var concorrencia = usuario.ConcurrencyStamp;
        var atualizado = usuario.AtualizadoEmUtc;

        usuario.DefinirMfaObrigatorio(false, Momento.AddDays(1));

        usuario.ConcurrencyStamp.Should().Be(concorrencia);
        usuario.AtualizadoEmUtc.Should().Be(atualizado);
    }

    [Fact]
    public void Recadastro_De_Mfa_Mantem_O_Segredo_Atual_Ate_Confirmar()
    {
        var usuario = Novo();
        usuario.PrepararMfa("segredo-antigo", Momento);
        usuario.ConfirmarMfa(Momento);

        // Recadastro iniciado e ABANDONADO: o segredo atual continua valendo (nunca fica sem segundo fator).
        usuario.PrepararMfa("segredo-novo", Momento.AddMinutes(1));
        usuario.MfaHabilitado.Should().BeTrue();
        usuario.SegredoMfaProtegido.Should().Be("segredo-antigo");
        usuario.SegredoMfaPendenteProtegido.Should().Be("segredo-novo");
        usuario.SegredoMfaEmConfirmacao.Should().Be("segredo-novo");

        // Confirmado: troca o segredo e rotaciona a marca de segurança.
        var marca = usuario.SecurityStamp;
        usuario.ConfirmarMfa(Momento.AddMinutes(2));
        usuario.SegredoMfaProtegido.Should().Be("segredo-novo");
        usuario.SegredoMfaPendenteProtegido.Should().BeNull();
        usuario.SecurityStamp.Should().NotBe(marca);
    }

    [Fact]
    public void Desabilitar_Mfa_Descarta_Tambem_O_Recadastro_Pendente()
    {
        var usuario = Novo();
        usuario.PrepararMfa("segredo-antigo", Momento);
        usuario.ConfirmarMfa(Momento);
        usuario.PrepararMfa("segredo-novo", Momento);

        usuario.DesabilitarMfa(Momento);

        usuario.SegredoMfaProtegido.Should().BeNull();
        usuario.SegredoMfaPendenteProtegido.Should().BeNull();
        usuario.MfaHabilitado.Should().BeFalse();
    }
}
