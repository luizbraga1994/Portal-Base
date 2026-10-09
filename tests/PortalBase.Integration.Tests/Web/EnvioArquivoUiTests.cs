using FluentAssertions;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Ingestao;
using PortalBase.Web.Ui.Dados;

namespace PortalBase.Integration.Tests.Web;

/// <summary>
/// Envio de planilha na UI (Prompt 74, §4/§5): mapeamento PURO do desfecho/exceção do caso de uso REAL
/// (IngerirArquivo, P08) para o estado de UI seguro, e restrições de UX declaradas pelo backend. A
/// autoridade (tenant/permissão/extensão/tamanho/conteúdo/antivírus/idempotência) permanece no caso de uso,
/// coberta por IngerirArquivoTests. Aqui garante-se que a UI NUNCA vaza detalhe e reflete o desfecho real.
/// Testes puros e determinísticos — sem MySQL/Docker/navegador.
/// </summary>
public sealed class EnvioArquivoUiTests
{
    // ---- A. Arquivo válido → Aceito ----
    [Fact]
    public void Aceito_Mapeia_Para_Ok_Com_ArquivoId()
    {
        var id = Guid.NewGuid();
        var vm = MapeadorEnvio.Mapear(ResultadoIngestao.Aceito(id));
        vm.Estado.Should().Be(EstadoEnvioUi.Ok);
        vm.ArquivoId.Should().Be(id);
    }

    // ---- B/C/D. Inválido / tamanho / extensão → Rejeitado (motivo GENÉRICO, sem detalhe interno) ----
    [Fact]
    public void Rejeitado_Mapeia_Para_Rejeitado_Sem_Vazar_Motivo_Interno()
    {
        var vm = MapeadorEnvio.Mapear(ResultadoIngestao.Rejeitado("extensao_nao_permitida"));
        vm.Estado.Should().Be(EstadoEnvioUi.Rejeitado);
        vm.ArquivoId.Should().BeNull();
        vm.Mensagem.Should().NotContain("extensao_nao_permitida"); // código interno nunca exibido (regra 16)
    }

    // ---- Idempotência (§7): duplicado é desfecho próprio, nunca "erro" ----
    [Fact]
    public void Duplicado_Mapeia_Para_Duplicado()
    {
        var vm = MapeadorEnvio.Mapear(ResultadoIngestao.Duplicado(null));
        vm.Estado.Should().Be(EstadoEnvioUi.Duplicado);
    }

    // ---- E. Usuário não autorizado → SemPermissao ----
    [Fact]
    public void Autorizacao_Negada_Mapeia_Para_SemPermissao()
        => MapeadorEnvio.MapearExcecao(new AutorizacaoNegadaException("dados.importar")).Estado.Should().Be(EstadoEnvioUi.SemPermissao);

    // ---- F. Tenant ausente → SemTenant ----
    [Fact]
    public void Tenant_Ausente_Mapeia_Para_SemTenant()
        => MapeadorEnvio.MapearExcecao(new TenantContextAusenteException()).Estado.Should().Be(EstadoEnvioUi.SemTenant);

    [Fact]
    public void SemTenant_Helper_Nao_Vaza_Detalhe()
    {
        var vm = MapeadorEnvio.SemTenant();
        vm.Estado.Should().Be(EstadoEnvioUi.SemTenant);
        vm.ArquivoId.Should().BeNull();
    }

    // ---- MFA e sessão inválida ----
    [Fact]
    public void Mfa_Requerido_Mapeia_Para_MfaRequerida()
        => MapeadorEnvio.MapearExcecao(new MfaRequeridoException()).Estado.Should().Be(EstadoEnvioUi.MfaRequerida);

    [Fact]
    public void Nao_Autenticado_Mapeia_Para_SemPermissao()
        => MapeadorEnvio.MapearExcecao(new NaoAutenticadoException()).Estado.Should().Be(EstadoEnvioUi.SemPermissao);

    // ---- H. Erro inesperado de ingestão → Erro (genérico, sem stack/segredo) ----
    [Fact]
    public void Erro_Inesperado_Mapeia_Para_Erro_Generico()
    {
        var vm = MapeadorEnvio.MapearExcecao(new InvalidOperationException("detalhe interno sensível"));
        vm.Estado.Should().Be(EstadoEnvioUi.Erro);
        vm.Mensagem.Should().NotContain("sensível"); // nunca repassa Exception.Message (regra 16)
    }

    // ---- Restrições de UX derivadas do backend (accept/tamanho/rótulo) ----
    [Fact]
    public void Restricoes_Derivam_Accept_Tamanho_E_Rotulo()
    {
        var r = new RestricoesEnvioVm(20L * 1024 * 1024, ["xlsx", "csv", "xml", "txt"]);
        r.Accept.Should().Be(".xlsx,.csv,.xml,.txt");
        r.MaxTamanhoMb.Should().Be(20);
        r.ExtensoesRotulo.Should().Be("xlsx, csv, xml, txt");
    }

    [Fact]
    public void Restricoes_Accept_Normaliza_Pontos_E_Espacos()
    {
        var r = new RestricoesEnvioVm(5L * 1024 * 1024, [" .XLSX ", "csv"]);
        r.Accept.Should().Be(".XLSX,.csv");
        r.MaxTamanhoMb.Should().Be(5);
    }
}
