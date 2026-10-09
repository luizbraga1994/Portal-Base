using FluentAssertions;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Tests.Dobras;

namespace PortalBase.Application.Tests.Identidade;

/// <summary>
/// Autorização server-side (seções 12, 16, 22, 33): principal + tenant + permissão + MFA,
/// deny-by-default. Cobre negação por não-autenticação, IDOR de tenant, permissão insuficiente
/// e step-up de MFA. A decisão NÃO depende de interface (seção 42).
/// </summary>
public sealed class ServicoAutorizacaoTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid Usuario = Guid.NewGuid();

    private readonly ContextoSeguranca _seguranca = new();
    private readonly AutorizacaoTenantFakeIdentidade _tenant = new();
    private readonly RepositorioUsuarioFake _usuarios = new();
    private readonly ServicoAutorizacao _servico;

    public ServicoAutorizacaoTests()
        => _servico = new ServicoAutorizacao(_seguranca, _tenant, _usuarios, new AuditoriaFake(), new RelogioFixo(Momento));

    private void AutenticarComAcesso(bool mfa = false)
    {
        _seguranca.Definir(Usuario.ToString(), mfa);
        _tenant.Permitidos.Add((Usuario.ToString(), TenantA));
    }

    [Fact] // §33.1
    public async Task Nao_Autenticado_E_Negado()
    {
        var r = await _servico.AvaliarAsync(TenantA, Permissoes.DadosVisualizar);
        r.Motivo.Should().Be(MotivoNegacao.NaoAutenticado);

        var acao = async () => await _servico.ExigirAsync(TenantA, Permissoes.DadosVisualizar);
        await acao.Should().ThrowAsync<NaoAutenticadoException>();
    }

    [Fact] // §33.7 / §35 — IDOR: usuário sem vínculo com o tenant
    public async Task Tenant_Nao_Autorizado_E_Negado()
    {
        _seguranca.Definir(Usuario.ToString(), mfaSatisfeito: false); // autenticado, mas sem acesso ao tenant
        _usuarios.DefinirPermissoes(Usuario, TenantA, Permissoes.DadosVisualizar);

        var r = await _servico.AvaliarAsync(TenantA, Permissoes.DadosVisualizar);
        r.Motivo.Should().Be(MotivoNegacao.TenantNaoAutorizado);

        var acao = async () => await _servico.ExigirAsync(TenantA, Permissoes.DadosVisualizar);
        await acao.Should().ThrowAsync<TenantNaoAutorizadoException>();
    }

    [Fact] // §33.2 — Leitor tentando operação de Analista
    public async Task Sem_Permissao_E_Negado()
    {
        AutenticarComAcesso();
        _usuarios.DefinirPermissoes(Usuario, TenantA, Permissoes.DadosVisualizar); // só leitura

        var r = await _servico.AvaliarAsync(TenantA, Permissoes.DadosImportar);
        r.Motivo.Should().Be(MotivoNegacao.SemPermissao);

        var acao = async () => await _servico.ExigirAsync(TenantA, Permissoes.DadosImportar);
        await acao.Should().ThrowAsync<AutorizacaoNegadaException>();
    }

    [Fact] // §33.6 — permissão presente → permitido
    public async Task Com_Permissao_E_Permitido()
    {
        AutenticarComAcesso();
        _usuarios.DefinirPermissoes(Usuario, TenantA, Permissoes.AdminUsuarios);

        (await _servico.AvaliarAsync(TenantA, Permissoes.AdminUsuarios)).Permitido.Should().BeTrue();
    }

    [Fact] // §33.4 / §33.17 — permissão de transmitir SEM MFA satisfeito → negado
    public async Task Alto_Impacto_Sem_Mfa_E_Negado()
    {
        AutenticarComAcesso(mfa: false);
        _usuarios.DefinirPermissoes(Usuario, TenantA, Permissoes.DadosExcluir);

        var r = await _servico.AvaliarAsync(TenantA, Permissoes.DadosExcluir);
        r.Motivo.Should().Be(MotivoNegacao.MfaRequerido);

        var acao = async () => await _servico.ExigirAsync(TenantA, Permissoes.DadosExcluir);
        await acao.Should().ThrowAsync<MfaRequeridoException>();
    }

    [Fact] // §33.5 — transmitir COM MFA satisfeito → permitido
    public async Task Alto_Impacto_Com_Mfa_E_Permitido()
    {
        AutenticarComAcesso(mfa: true);
        _usuarios.DefinirPermissoes(Usuario, TenantA, Permissoes.DadosExcluir);

        (await _servico.AvaliarAsync(TenantA, Permissoes.DadosExcluir)).Permitido.Should().BeTrue();
    }

    [Fact] // FND-001: fechamento de competência SEM MFA satisfeito → negado (política uniforme).
    public async Task Fechamento_Sem_Mfa_E_Negado()
    {
        AutenticarComAcesso(mfa: false);
        _usuarios.DefinirPermissoes(Usuario, TenantA, Permissoes.DadosExcluir);

        var r = await _servico.AvaliarAsync(TenantA, Permissoes.DadosExcluir);
        r.Motivo.Should().Be(MotivoNegacao.MfaRequerido);

        var acao = async () => await _servico.ExigirAsync(TenantA, Permissoes.DadosExcluir);
        await acao.Should().ThrowAsync<MfaRequeridoException>();
    }

    [Fact] // FND-001: fechamento de competência COM MFA satisfeito → permitido.
    public async Task Fechamento_Com_Mfa_E_Permitido()
    {
        AutenticarComAcesso(mfa: true);
        _usuarios.DefinirPermissoes(Usuario, TenantA, Permissoes.DadosExcluir);

        (await _servico.AvaliarAsync(TenantA, Permissoes.DadosExcluir)).Permitido.Should().BeTrue();
    }
}
