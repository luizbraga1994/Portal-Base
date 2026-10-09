using FluentAssertions;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Identidade;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Application.Tests.Identidade;

/// <summary>Ativação, confirmação, step-up e recuperação de MFA (seções 18, 19, 36).</summary>
public sealed class GestaoMfaTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly RepositorioUsuarioFake _usuarios = new();
    private readonly ProvedorTotpFake _totp = new();
    private readonly GestaoMfa _mfa;
    private readonly Usuario _usuario;

    public GestaoMfaTests()
    {
        _usuario = Usuario.Criar("t", "Trans", "t@exemplo.com", Momento);
        _usuario.DefinirCredencial("H:x", Momento);
        _usuarios.Usuarios.Add(_usuario);

        _mfa = new GestaoMfa(
            _usuarios,
            _totp,
            new ProtetorSegredoMfaFake(),
            new GeradorTokensFake(),
            new HashDeterministicoFake(),
            new RegistroConsumoTotpFake(),
            new UnitOfWorkFake(),
            new AuditoriaFake(),
            new RelogioFixo(Momento));
    }

    private async Task<AtivacaoMfaConcluida> AtivarAsync()
    {
        await _mfa.IniciarAtivacaoAsync(_usuario.Id);
        return await _mfa.ConfirmarAtivacaoAsync(_usuario.Id, _totp.CodigoValido);
    }

    [Fact]
    public async Task Iniciar_Guarda_Segredo_Protegido_Pendente()
    {
        var provisionamento = await _mfa.IniciarAtivacaoAsync(_usuario.Id);

        provisionamento.SegredoBase32.Should().NotBeNullOrEmpty();
        _usuario.SegredoMfaProtegido.Should().NotBeNullOrEmpty();
        _usuario.MfaHabilitado.Should().BeFalse();
    }

    [Fact]
    public async Task Confirmar_Com_Codigo_Valido_Habilita_E_Gera_Recuperacao()
    {
        var conclusao = await AtivarAsync();

        _usuario.MfaHabilitado.Should().BeTrue();
        conclusao.CodigosRecuperacao.Should().HaveCountGreaterThan(0);
        _usuarios.CodigosMfa.Should().HaveCount(conclusao.CodigosRecuperacao.Count);
    }

    [Fact]
    public async Task Confirmar_Com_Codigo_Invalido_Nao_Habilita()
    {
        await _mfa.IniciarAtivacaoAsync(_usuario.Id);

        var acao = async () => await _mfa.ConfirmarAtivacaoAsync(_usuario.Id, "000000");
        await acao.Should().ThrowAsync<MfaInvalidoException>();
        _usuario.MfaHabilitado.Should().BeFalse();
    }

    [Fact] // §33.16 — step-up com código inválido é negado
    public async Task Validar_Com_Codigo_Invalido_E_Negado()
    {
        await AtivarAsync();

        var acao = async () => await _mfa.ValidarAsync(_usuario.Id, "999999");
        await acao.Should().ThrowAsync<MfaInvalidoException>();
    }

    [Fact]
    public async Task Validar_Com_Codigo_Valido_Passa()
    {
        await AtivarAsync();

        var acao = async () => await _mfa.ValidarAsync(_usuario.Id, _totp.CodigoValido);
        await acao.Should().NotThrowAsync();
    }

    [Fact] // Prompt 68 — ANTI-REPLAY: o MESMO código TOTP não pode ser aceito duas vezes na janela de validade
    public async Task Validar_Mesmo_Codigo_Totp_Duas_Vezes_E_Negado_Replay()
    {
        await AtivarAsync();

        var primeira = async () => await _mfa.ValidarAsync(_usuario.Id, _totp.CodigoValido);
        await primeira.Should().NotThrowAsync();

        // Reapresentar o mesmo código (mesmo passo) dentro da validade ⇒ replay ⇒ negado.
        var segunda = async () => await _mfa.ValidarAsync(_usuario.Id, _totp.CodigoValido);
        await segunda.Should().ThrowAsync<MfaInvalidoException>();
    }

    [Fact] // §36 — código de recuperação é uso único
    public async Task Codigo_De_Recuperacao_Funciona_Uma_Vez()
    {
        var conclusao = await AtivarAsync();
        var codigo = conclusao.CodigosRecuperacao[0];

        var primeira = async () => await _mfa.ValidarAsync(_usuario.Id, codigo);
        await primeira.Should().NotThrowAsync();

        var segunda = async () => await _mfa.ValidarAsync(_usuario.Id, codigo);
        await segunda.Should().ThrowAsync<MfaInvalidoException>(); // já consumido
    }
}
