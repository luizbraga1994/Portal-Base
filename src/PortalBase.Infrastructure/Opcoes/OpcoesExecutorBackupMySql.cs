namespace PortalBase.Infrastructure.Opcoes;

/// <summary>
/// Configuração do EXECUTOR FÍSICO de backup/restore por processo <c>mysqldump</c>/<c>mysql</c>
/// (Prompt 50, Fases 36-38). Fica DESABILITADO por padrão (fail-closed): só é ligado explicitamente
/// quando o host tem MySQL + ferramentas e o operador quer o executor real (o <c>ExecutorBackupIndisponivel</c>
/// permanece o default seguro). Nenhum segredo aqui — o servidor/usuário vêm de <c>BancoTenant</c> e a
/// senha do COFRE por referência; nunca na linha de comando (as credenciais vão por arquivo de opções 0600).
/// </summary>
public sealed class OpcoesExecutorBackupMySql
{
    public const string Secao = "Backup:ExecutorMySql";

    /// <summary>Liga o executor físico real. Padrão <c>false</c> ⇒ backup/restore físico BLOCKED (fail-closed).</summary>
    public bool Habilitado { get; set; }

    /// <summary>Binário do <c>mysqldump</c> (nome no PATH ou caminho absoluto).</summary>
    public string CaminhoMysqldump { get; set; } = "mysqldump";

    /// <summary>Binário do <c>mysql</c> (nome no PATH ou caminho absoluto).</summary>
    public string CaminhoMysql { get; set; } = "mysql";

    /// <summary>Timeout (segundos) de cada processo de dump/restore. Mínimo efetivo de 5s.</summary>
    public int TimeoutSegundos { get; set; } = 600;
}
