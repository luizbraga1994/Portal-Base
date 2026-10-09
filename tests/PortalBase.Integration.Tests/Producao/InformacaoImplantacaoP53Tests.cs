using FluentAssertions;
using PortalBase.Infrastructure.Producao;

namespace PortalBase.Integration.Tests.Producao;

/// <summary>
/// Prompt 53 — identidade de implantação/release (Fase 3). Garante que VERSION → COMMIT → BUILD → ENV são
/// coletados de forma segura (nunca vazios, nunca segredo) e que commit/timestamp vêm das variáveis de
/// ambiente injetadas pela imagem/CI (<c>PORTALBASE_BUILD_*</c>). Esta classe é a ÚNICA que toca essas
/// variáveis, então rodam sequencialmente sem interferir em outros testes.
/// </summary>
public sealed class InformacaoImplantacaoP53Tests
{
    [Fact]
    public void Coletar_Retorna_Campos_Nao_Vazios()
    {
        var info = InformacaoImplantacao.Coletar("Testing");

        info.Versao.Should().NotBeNullOrWhiteSpace();
        info.Ambiente.Should().Be("Testing");
        info.Host.Should().NotBeNullOrWhiteSpace();
        info.ParaLog().Should().Contain("versao=").And.Contain("ambiente=Testing");
        // Nunca deve expor segredo/connection string na linha de log.
        info.ParaLog().Should().NotContainAny("password", "Server=", "Bearer", "connectionstring");
    }

    [Fact]
    public void Coletar_Le_Commit_E_Build_Do_Ambiente()
    {
        try
        {
            Environment.SetEnvironmentVariable("PORTALBASE_BUILD_SHA", "abc1234");
            Environment.SetEnvironmentVariable("PORTALBASE_BUILD_TIMESTAMP", "2026-09-17T00:00:00Z");
            Environment.SetEnvironmentVariable("PORTALBASE_BUILD_VERSION", "9.9.9-rc1");

            var info = InformacaoImplantacao.Coletar("Production");

            info.CommitSha.Should().Be("abc1234");
            info.BuildTimestamp.Should().Be("2026-09-17T00:00:00Z");
            info.Versao.Should().Be("9.9.9-rc1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PORTALBASE_BUILD_SHA", null);
            Environment.SetEnvironmentVariable("PORTALBASE_BUILD_TIMESTAMP", null);
            Environment.SetEnvironmentVariable("PORTALBASE_BUILD_VERSION", null);
        }
    }

    [Fact]
    public void Coletar_Sem_Ambiente_Cai_Para_Desconhecido()
    {
        var info = InformacaoImplantacao.Coletar(null);
        info.Ambiente.Should().Be("desconhecido");
        info.CommitSha.Should().NotBeNullOrWhiteSpace(); // "desconhecido" quando não injetado
    }
}
