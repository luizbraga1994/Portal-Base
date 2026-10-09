using Microsoft.Extensions.Configuration;
using PortalBase.Application.Producao;
using PortalBase.Infrastructure.DependencyInjection;
using PortalBase.Infrastructure.Opcoes;

namespace PortalBase.Infrastructure.Producao;

/// <summary>
/// Pré-checagem de startup (Prompt 33, seções 9/10) — FAIL-FAST em ambiente produtivo: se a
/// configuração essencial estiver ausente, o serviço crítico NÃO sobe em estado operacionalmente
/// enganoso. Lê apenas indicadores de PRESENÇA da configuração (nunca valores de segredo/senha/
/// connection string — §8/§10) e delega a decisão ao validador puro da camada de aplicação. A
/// mensagem de falha lista TODAS as chaves faltantes de uma vez (melhor operação), sem expor valores.
/// </summary>
public static class PreflightProducao
{
    /// <summary>
    /// Constrói o estado (presença) da configuração essencial a partir de <paramref name="configuration"/>
    /// e do nome do ambiente, e retorna o resultado da validação.
    /// </summary>
    public static ResultadoValidacaoConfiguracao Avaliar(IConfiguration configuration, string? nomeAmbiente)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var ambiente = ClassificadorAmbiente.Classificar(nomeAmbiente);

        var conexaoCatalogo = !string.IsNullOrWhiteSpace(
            configuration.GetConnectionString(ServicosInfrastructure.NomeConexaoCatalogo));
        var modoVault = configuration.GetValue<string?>($"{OpcoesVault.Secao}:{nameof(OpcoesVault.Modo)}") ?? string.Empty;
        var diretorioArmazenamento = !string.IsNullOrWhiteSpace(
            configuration.GetValue<string?>($"{OpcoesArmazenamento.Secao}:{nameof(OpcoesArmazenamento.DiretorioRaiz)}"));
        var conexaoMigracaoCatalogo = !string.IsNullOrWhiteSpace(
            configuration.GetConnectionString(Persistencia.Catalogo.GarantiaCatalogoDesenvolvimento.NomeConexaoMigracao));

        var diretorioCofreWindows = !string.IsNullOrWhiteSpace(
            configuration.GetValue<string?>($"{OpcoesVault.Secao}:{nameof(OpcoesVault.DiretorioCofreWindows)}"));

        var estado = new EstadoConfiguracaoProducao(
            ambiente, conexaoCatalogo, modoVault, diretorioArmazenamento,
            conexaoMigracaoCatalogo, diretorioCofreWindows);

        return ValidadorConfiguracaoProducao.Validar(estado);
    }

    /// <summary>
    /// Exige a configuração essencial em ambiente produtivo — lança <see cref="InvalidOperationException"/>
    /// (fail-fast) listando as chaves faltantes se algo obrigatório estiver ausente. Em ambiente não
    /// produtivo, é um no-op. <paramref name="dica"/> (opcional) vai no fim da mensagem: onde a configuração é esperada.
    /// </summary>
    public static void Exigir(IConfiguration configuration, string? nomeAmbiente, string? dica = null)
    {
        var resultado = Avaliar(configuration, nomeAmbiente);
        if (resultado.Ok)
        {
            return;
        }

        var itens = string.Join("; ", resultado.Faltantes.Select(f => $"{f.Chave} ({f.Detalhe})"));
        throw new InvalidOperationException(
            $"Configuração de produção incompleta — startup abortado (fail-fast). Faltando: {itens}.{dica}");
    }
}
