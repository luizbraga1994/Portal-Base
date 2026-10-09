using System.Globalization;
using PortalBase.Application.Abstracoes;
using PortalBase.Infrastructure.Backup;
using PortalBase.Infrastructure.Segredos;
using PortalBase.Infrastructure.Tempo;

namespace PortalBase.Web.Hospedagem;

/// <summary>
/// Comando de RECUPERAÇÃO DE DESASTRE <c>PortalBase.Web.exe decifrar-backup</c> (BKP-001, auditoria de 08/10/2026):
/// decifra um artefato de backup (<c>.bak.enc</c>) para restaurar o catálogo, a base de um contribuinte ou os arquivos
/// dele — sem banco e sem subir o site (o catálogo pode ser justamente o que se perdeu). A chave dos backups vem de:
/// <list type="bullet">
/// <item><c>--pacote-cofre &lt;arquivo.portalbasecofre&gt;</c>: o pacote exportado pelo <c>cofre-windows.ps1 -Acao Exportar</c>
/// (senha digitada sem eco) — funciona em QUALQUER máquina, inclusive se o servidor se perdeu;</item>
/// <item><c>--cofre &lt;pasta&gt;</c>: uma pasta de cofre deste servidor (Windows; ex.: restaurada com <c>-Acao Restaurar</c>);</item>
/// <item>sem nenhum dos dois: o cofre configurado do portal (o mesmo do site).</item>
/// </list>
/// A referência da chave é <c>--chave &lt;nome&gt;</c> ou <c>Backup:ChaveCriptografiaRef</c>. Nunca escreve segredo; nunca
/// sobrescreve; nunca deixa saída parcial. Como no <see cref="ComandoPrimeiroAdministrador"/>, o console é a interface
/// de quem administra o servidor (única exceção ao banimento de <c>Console.Write*</c> — regras 8/16).
/// </summary>
internal static class ComandoDecifrarBackup
{
    public const string Nome = "decifrar-backup";

    private const string Uso =
        "Uso: PortalBase.Web.exe decifrar-backup --arquivo <artefato.bak.enc> --saida <arquivo> [--chave <nome no cofre>] " +
        "[--pacote-cofre <arquivo.portalbasecofre> | --cofre <pasta do cofre>]";

    public static bool Solicitado(string[] args) => args.Length > 0 && string.Equals(args[0], Nome, StringComparison.OrdinalIgnoreCase);

#pragma warning disable RS0030 // Comando de recuperação: o console é a interface (ver resumo da classe).
    public static async Task<int> ExecutarAsync(string[] args, IConfiguration configuracao, IHostEnvironment ambiente)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuracao);
        ArgumentNullException.ThrowIfNull(ambiente);

        if (!TentarLerOpcoes(args, out var opcoes))
        {
            Console.Error.WriteLine(Uso);
            return 1;
        }

        var arquivo = opcoes.GetValueOrDefault("--arquivo");
        var saida = opcoes.GetValueOrDefault("--saida");
        var chave = opcoes.GetValueOrDefault("--chave") ?? configuracao["Backup:ChaveCriptografiaRef"];
        var pacote = opcoes.GetValueOrDefault("--pacote-cofre");
        var cofre = opcoes.GetValueOrDefault("--cofre");
        if (string.IsNullOrWhiteSpace(arquivo) || string.IsNullOrWhiteSpace(saida) || (pacote is not null && cofre is not null))
        {
            Console.Error.WriteLine(Uso);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(chave))
        {
            Console.Error.WriteLine("Informe --chave com o nome da chave dos backups no cofre (ex.: portalbase-prod-chave-backup).");
            return 1;
        }

        try
        {
            var segredos = ObterSegredos(pacote, cofre, configuracao, ambiente);
            if (segredos is null)
            {
                return 2;
            }

            var r = await DecifradorArtefatoBackup.DecifrarAsync(segredos, chave, arquivo, saida).ConfigureAwait(false);
            Console.WriteLine($"Artefato: SHA-256 {r.HashArtefatoSha256} (confira com o hash do backup em Operações → Backups ou no catálogo).");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Decifrado: {r.BytesDecifrados} bytes em {Path.GetFullPath(saida)}"));
            Console.WriteLine(r.Conteudo switch
            {
                ConteudoArtefatoBackup.DumpSql =>
                    "Conteúdo: dump SQL do MySQL. Restaure numa base VAZIA: mysql --default-character-set=utf8mb4 -u <conta admin> -p <base> < <saída>",
                ConteudoArtefatoBackup.PacoteZip =>
                    "Conteúdo: arquivos do contribuinte (ZIP). Extraia 'arquivos\\' em <Armazenamento:DiretorioRaiz>\\<id do contribuinte sem hífens>\\.",
                _ => "Conteúdo: formato não reconhecido (confira se o artefato é mesmo um backup do Portal Base).",
            });
            return 0;
        }
        catch (Exception excecao) when (excecao is InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Mensagens próprias, sem segredo nem detalhe interno (nomes de segredo e caminhos informados pelo operador).
            Console.Error.WriteLine(excecao.Message);
            return 3;
        }
    }

    private static IProvedorSegredos? ObterSegredos(string? pacote, string? cofre, IConfiguration configuracao, IHostEnvironment ambiente)
    {
        if (pacote is not null)
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Rode este comando num console interativo (a senha do pacote é digitada sem aparecer na tela).");
                return null;
            }

            var bytes = File.ReadAllBytes(Path.GetFullPath(pacote));
            var senha = ComandoPrimeiroAdministrador.LerOculto("Senha do pacote do cofre");
            return new ProvedorSegredosPacote(PacoteCofreExportado.Abrir(bytes, senha));
        }

        if (cofre is not null)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("--cofre só funciona no Windows (DPAPI da máquina). Em outra máquina, use --pacote-cofre.");
            }

            return new ProvedorSegredosCofreWindows(cofre, new ProtetorDpapiMaquina());
        }

        // O cofre configurado do portal (mesma composição do site: CofreWindows, KeyVault ou, fora de produção, configuração).
        var servicos = new ServiceCollection();
        servicos.AddSingleton(configuracao);
        servicos.AddSingleton<IRelogio, RelogioSistema>();
        servicos.AddSegredos(configuracao, ambiente.IsProduction());
        return servicos.BuildServiceProvider().GetRequiredService<IProvedorSegredos>();
    }

    private static bool TentarLerOpcoes(string[] args, out Dictionary<string, string> opcoes)
    {
        string[] conhecidas = ["--arquivo", "--saida", "--chave", "--pacote-cofre", "--cofre"];
        opcoes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i += 2)
        {
            var nome = args[i];
            if (!conhecidas.Contains(nome, StringComparer.OrdinalIgnoreCase) || i + 1 >= args.Length || opcoes.ContainsKey(nome))
            {
                return false;
            }

            opcoes[nome.ToLowerInvariant()] = args[i + 1];
        }

        return true;
    }
#pragma warning restore RS0030
}
