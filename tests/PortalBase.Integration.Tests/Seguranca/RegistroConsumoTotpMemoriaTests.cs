using FluentAssertions;
using PortalBase.Infrastructure.Seguranca;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>
/// ANTI-REPLAY de TOTP (Prompt 68) — a implementação REAL em memória. Prova que um passo consumido não é
/// aceito de novo (nem um anterior), que usuários são independentes, e que um passo posterior avança normalmente.
/// </summary>
public sealed class RegistroConsumoTotpMemoriaTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Primeiro_Consumo_De_Um_Passo_E_Aceito()
    {
        var registro = new RegistroConsumoTotpMemoria();
        registro.TentarConsumir(Guid.NewGuid(), 1000, Agora).Should().BeTrue();
    }

    [Fact]
    public void Mesmo_Passo_Repetido_E_Rejeitado()
    {
        var registro = new RegistroConsumoTotpMemoria();
        var usuario = Guid.NewGuid();
        registro.TentarConsumir(usuario, 1000, Agora).Should().BeTrue();
        registro.TentarConsumir(usuario, 1000, Agora).Should().BeFalse(); // replay
    }

    [Fact]
    public void Passo_Anterior_E_Rejeitado()
    {
        var registro = new RegistroConsumoTotpMemoria();
        var usuario = Guid.NewGuid();
        registro.TentarConsumir(usuario, 1000, Agora).Should().BeTrue();
        registro.TentarConsumir(usuario, 999, Agora).Should().BeFalse(); // não pode retroceder
    }

    [Fact]
    public void Passo_Posterior_Avanca()
    {
        var registro = new RegistroConsumoTotpMemoria();
        var usuario = Guid.NewGuid();
        registro.TentarConsumir(usuario, 1000, Agora).Should().BeTrue();
        registro.TentarConsumir(usuario, 1001, Agora.AddSeconds(30)).Should().BeTrue();
    }

    [Fact]
    public void Usuarios_Sao_Independentes()
    {
        var registro = new RegistroConsumoTotpMemoria();
        registro.TentarConsumir(Guid.NewGuid(), 1000, Agora).Should().BeTrue();
        registro.TentarConsumir(Guid.NewGuid(), 1000, Agora).Should().BeTrue(); // outro usuário, mesmo passo, ok
    }
}
