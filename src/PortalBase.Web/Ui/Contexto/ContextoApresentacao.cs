using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using PortalBase.Application.Producao;
using PortalBase.Web.Ui.Autorizacao;

namespace PortalBase.Web.Ui.Contexto;

/// <summary>Contexto de apresentação exibido no topo (tenant/contribuinte/ambiente).</summary>
public interface IContextoApresentacao
{
    AmbienteImplantacao Ambiente { get; }

    /// <summary>Rótulo do ambiente (ex.: "Produção Restrita").</summary>
    string RotuloAmbiente { get; }

    /// <summary>Classe CSS do selo de ambiente (ex.: "env-prod-restrita").</summary>
    string ClasseAmbiente { get; }

    /// <summary>Se está em modo de demonstração (dados simulados e rotulados).</summary>
    bool Demonstracao { get; }

    /// <summary>Contribuinte/tenant atual, quando selecionado; caso contrário, nulo.</summary>
    string? Tenant { get; }
    string? Contribuinte { get; }
}

/// <inheritdoc />
public sealed class ContextoApresentacao : IContextoApresentacao
{
    public ContextoApresentacao(IWebHostEnvironment ambiente, IAutorizacaoVisual visual)
    {
        ArgumentNullException.ThrowIfNull(ambiente);
        ArgumentNullException.ThrowIfNull(visual);

        Ambiente = ClassificadorAmbiente.Classificar(ambiente.EnvironmentName);
        Demonstracao = visual.Demonstracao;

        // Sem tenant resolvido não há contribuinte (fail-closed). Em demonstração,
        // exibe rótulos simulados para ilustrar o contexto — nunca dados reais.
        if (Demonstracao)
        {
            Tenant = "Contribuinte Demonstração LTDA";
            Contribuinte = "12.345.678/0001-90";
        }
    }

    public AmbienteImplantacao Ambiente { get; }

    public bool Demonstracao { get; }

    public string? Tenant { get; }

    public string? Contribuinte { get; }

    public string RotuloAmbiente => Ambiente switch
    {
        AmbienteImplantacao.Development => "Desenvolvimento",
        AmbienteImplantacao.Test => "Teste",
        AmbienteImplantacao.Staging => "Homologação",
        AmbienteImplantacao.ProducaoRestrita => "Produção Restrita",
        AmbienteImplantacao.Producao => "Produção",
        _ => "Desconhecido",
    };

    public string ClasseAmbiente => Ambiente switch
    {
        AmbienteImplantacao.Development => "env-dev",
        AmbienteImplantacao.Test => "env-teste",
        AmbienteImplantacao.Staging => "env-homolog",
        AmbienteImplantacao.ProducaoRestrita => "env-prod-restrita",
        AmbienteImplantacao.Producao => "env-prod",
        _ => "env-dev",
    };
}
