using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using PortalBase.Application.Identidade;

namespace PortalBase.Application.Tests.Seguranca;

/// <summary>
/// P74-R1 (GAP-P74R1-01): <c>GestaoMfa.DesabilitarAsync(usuarioId)</c> desligava o MFA da identidade GLOBAL de
/// qualquer usuário sem autorização alguma (nem tenant, nem permissão, nem step-up) e auditava o próprio alvo
/// como autor. Não tinha consumidor; foi removido. O ciclo de vida global da identidade só pode voltar como caso
/// de uso com os pré-requisitos da <c>docs/ADR-IDENTIDADE-GLOBAL-VS-ACESSO-TENANT.md</c> §5 — a revisão §8
/// (07/10/2026) autorizou um único: <c>GestaoUsuarios.RemoverMfaAsync</c>, com step-up de MFA e a regra de conta
/// exclusiva do contribuinte. Estes testes falham se o atalho reaparecer em qualquer outro lugar fora do domínio,
/// ou se esse caso de uso perder as travas.
/// </summary>
public sealed class GuardaIdentidadeGlobalTests
{
    private static readonly Regex ChamadaDesabilitarMfa = new(@"\.DesabilitarMfa\s*\(", RegexOptions.Compiled);

    /// <summary>O único caso de uso autorizado (ADR §8).</summary>
    private static readonly string CasoDeUsoAutorizado = Path.Combine("PortalBase.Application", "Identidade", "GestaoUsuarios.cs");

    [Fact]
    public void GestaoMfa_Expoe_Somente_Operacoes_Do_Proprio_Usuario()
    {
        var publicos = typeof(GestaoMfa)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        publicos.Should().BeEquivalentTo(
            [nameof(GestaoMfa.IniciarAtivacaoAsync), nameof(GestaoMfa.ConfirmarAtivacaoAsync), nameof(GestaoMfa.ValidarAsync)],
            "desativar/resetar o MFA de outro usuário é ciclo de vida GLOBAL da identidade (ADR §5), não autoatendimento");
    }

    [Fact]
    public void Nenhuma_Camada_Fora_Do_Dominio_Desabilita_O_Mfa_Da_Identidade_Global()
    {
        var src = LocalizarSrc();
        var dominio = Path.Combine(src, "PortalBase.Domain") + Path.DirectorySeparatorChar;
        var arquivos = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(caminho => caminho.EndsWith(".cs", StringComparison.Ordinal) || caminho.EndsWith(".razor", StringComparison.Ordinal))
            .Where(caminho => !caminho.StartsWith(dominio, StringComparison.Ordinal)
                           && !caminho.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !caminho.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var achados = new List<string>();
        foreach (var arquivo in arquivos)
        {
            var linhas = File.ReadAllLines(arquivo);
            for (var i = 0; i < linhas.Length; i++)
            {
                if (ChamadaDesabilitarMfa.IsMatch(linhas[i])
                    && !string.Equals(Path.GetRelativePath(src, arquivo), CasoDeUsoAutorizado, StringComparison.Ordinal))
                {
                    achados.Add($"{Path.GetRelativePath(src, arquivo)}:{i + 1}");
                }
            }
        }

        achados.Should().BeEmpty(
            "Usuario.DesabilitarMfa afeta todos os contribuintes do usuário e só pode ser chamado por um caso de uso de "
            + "plataforma com permissão própria, step-up de MFA e auditoria do operador (ADR-IDENTIDADE-GLOBAL §5/§8)");
    }

    [Fact]
    public void O_Unico_Caso_De_Uso_Que_Remove_O_Mfa_Mantem_As_Travas_Do_Adr()
    {
        var codigo = File.ReadAllText(Path.Combine(LocalizarSrc(), CasoDeUsoAutorizado)).ReplaceLineEndings("\n");
        var inicio = codigo.IndexOf("public async Task<ResultadoGestao> RemoverMfaAsync(", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(0, "o caso de uso autorizado é GestaoUsuarios.RemoverMfaAsync");
        var fim = codigo.IndexOf("\n    }\n", inicio, StringComparison.Ordinal);
        var corpo = codigo[inicio..fim];

        ChamadaDesabilitarMfa.Matches(codigo).Should().ContainSingle("só RemoverMfaAsync desliga o MFA neste arquivo");
        ChamadaDesabilitarMfa.IsMatch(corpo).Should().BeTrue();
        corpo.Should().Contain("ExigirAdministradorAsync(", "permissão admin.usuarios no contribuinte");
        corpo.Should().Contain("throw new MfaRequeridoException()", "step-up de MFA obrigatório");
        corpo.Should().Contain("GarantirNaoEhOAtor(", "ninguém remove o próprio MFA por aqui");
        corpo.Should().Contain("PodeGerirIdentidadeAsync(", "conta compartilhada com outro contribuinte só pela plataforma");
        corpo.Should().Contain("RemoverCodigosRecuperacaoMfaAsync(", "os códigos de recuperação também são apagados");
        corpo.Should().Contain("AcaoAuditoria.MfaRemovedByAdministrator", "auditoria específica");
        corpo.IndexOf("PodeGerirIdentidadeAsync(", StringComparison.Ordinal)
            .Should().BeLessThan(corpo.IndexOf(".DesabilitarMfa(", StringComparison.Ordinal), "as travas vêm antes da remoção");
    }

    private static string LocalizarSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortalBase.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("a raiz do repositório (PortalBase.sln) deve ser localizável a partir do teste");
        var src = Path.Combine(dir!.FullName, "src");
        Directory.Exists(src).Should().BeTrue();
        return src;
    }
}
