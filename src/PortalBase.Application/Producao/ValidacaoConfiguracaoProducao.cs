namespace PortalBase.Application.Producao;

/// <summary>
/// Estado (presença) da configuração essencial para um ambiente produtivo (Prompt 33, seções 9/10).
/// Carrega APENAS indicadores de PRESENÇA — nunca valores de segredo, senha ou connection string
/// (§8/§10). O host preenche estes indicadores a partir da configuração e os passa ao validador.
/// </summary>
public sealed record EstadoConfiguracaoProducao(
    AmbienteImplantacao Ambiente,
    bool ConexaoCatalogoPresente,
    string ModoVault,
    bool DiretorioArmazenamentoPresente,
    bool ConexaoMigracaoCatalogoPresente = false,
    bool DiretorioCofreWindowsPresente = false);

/// <summary>Um requisito de configuração e se está satisfeito. <see cref="Detalhe"/> nunca contém segredo.</summary>
public sealed record RequisitoConfiguracao(string Chave, bool Satisfeito, string Detalhe);

/// <summary>Resultado da validação de configuração de produção — <see cref="Ok"/> só quando nada falta.</summary>
public sealed record ResultadoValidacaoConfiguracao(bool Ok, IReadOnlyList<RequisitoConfiguracao> Faltantes);

/// <summary>
/// Valida, de forma FAIL-FAST (Prompt 33, seção 9), que a configuração essencial existe antes de um
/// serviço crítico subir em ambiente produtivo. Em ambiente NÃO produtivo, não impõe exigências
/// (permite Dev/Test sem cofre/endpoint). Em produção, exige: banco de catálogo, cofre gerenciado
/// (<c>Vault:Modo=KeyVault</c>) ou cofre local do Windows (<c>Vault:Modo=CofreWindows</c> + pasta), e diretório de armazenamento privado. Só
/// reporta PRESENÇA/AUSÊNCIA; nunca expõe o valor de qualquer segredo.
/// </summary>
public static class ValidadorConfiguracaoProducao
{
    private const string ModoKeyVault = "KeyVault";
    private const string ModoCofreWindows = "CofreWindows";

    public static ResultadoValidacaoConfiguracao Validar(EstadoConfiguracaoProducao estado)
    {
        ArgumentNullException.ThrowIfNull(estado);

        var faltantes = new List<RequisitoConfiguracao>();

        if (!ClassificadorAmbiente.EhProdutivo(estado.Ambiente))
        {
            // Dev/Test/Staging: sem exigências fail-fast (o startup normal já reporta indisponibilidades).
            return new ResultadoValidacaoConfiguracao(true, faltantes);
        }

        if (!estado.ConexaoCatalogoPresente)
        {
            faltantes.Add(new RequisitoConfiguracao("ConnectionStrings:Catalogo", false, "banco de catálogo obrigatório em produção"));
        }

        var cofreWindows = string.Equals(estado.ModoVault, ModoCofreWindows, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(estado.ModoVault, ModoKeyVault, StringComparison.OrdinalIgnoreCase) && !cofreWindows)
        {
            faltantes.Add(new RequisitoConfiguracao("Vault:Modo", false,
                "cofre obrigatório em produção: KeyVault (Azure) ou CofreWindows (cofre local do Windows); provedor por configuração proibido"));
        }
        else if (cofreWindows && !estado.DiretorioCofreWindowsPresente)
        {
            faltantes.Add(new RequisitoConfiguracao("Vault:DiretorioCofreWindows", false, "pasta privada do cofre local obrigatória com Vault:Modo=CofreWindows"));
        }

        if (!estado.DiretorioArmazenamentoPresente)
        {
            faltantes.Add(new RequisitoConfiguracao("Armazenamento:DiretorioRaiz", false, "diretório privado de armazenamento obrigatório em produção"));
        }

        // Credencial com DDL no catálogo é ferramenta de DESENVOLVIMENTO (recriar a base apagada): em produção, migrations
        // só pelo pipeline, e a aplicação roda só com DML (menor privilégio).
        if (estado.ConexaoMigracaoCatalogoPresente)
        {
            faltantes.Add(new RequisitoConfiguracao("ConnectionStrings:CatalogoMigracao", false, "credencial de migração do catálogo é proibida em produção (remova-a; migrations só pelo pipeline)"));
        }

        return new ResultadoValidacaoConfiguracao(faltantes.Count == 0, faltantes);
    }
}
