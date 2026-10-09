using System.Globalization;

namespace PortalBase.Application.Observabilidade;

// ============================================================================
// ALERTAS OPERACIONAIS (OBS-004, auditoria de 08/10/2026) — antes, os alertas existiam só no documento: backup que não
// rodou, Worker parado e disco cheio ficavam em silêncio até alguém abrir a tela. Aqui ficam
// as REGRAS (puras, testáveis); a coleta e o disparo estão em CicloAlertasOperacionais. Só metadados técnicos: nunca
// payload, segredo, caminho de pasta ou dado pessoal (§3/§80).
// ============================================================================

/// <summary>De onde vem a condição do alerta. Uma fonte que não pôde ser lida não "normaliza" os alertas dela.</summary>
public enum FonteAlerta
{
    Worker = 1,
    Backup = 2,
    Disco = 3,
    Email = 5,
}

/// <summary>Espaço livre de um disco usado pelo portal (um registro por disco, com os usos que ele tem).</summary>
public sealed record EspacoDisco(string Unidade, string Usos, long LivreBytes, long TotalBytes)
{
    public double PercentualLivre => TotalBytes <= 0 ? 100 : LivreBytes * 100.0 / TotalBytes;
}

/// <summary>Mede os discos das pastas do portal (arquivos, temporários, backups, cópia externa local, logs).</summary>
public interface IVerificadorEspacoDisco
{
    /// <summary>Um registro por disco; pasta não configurada, inexistente ou de rede (não medível) fica de fora.</summary>
    IReadOnlyList<EspacoDisco> Medir();
}

/// <summary>
/// O que a rotina conseguiu ler nesta volta. <c>null</c> = fonte não avaliada (desligada ou com falha de leitura):
/// nada é concluído sobre ela — nem alerta novo, nem "normalizado".
/// </summary>
public sealed record EntradasAlertas
{
    public SituacaoServicoWorker? Worker { get; init; }

    /// <summary>Houve backup do catálogo concluído (ou em andamento) dentro do prazo? <c>null</c> = backup agendado desligado.</summary>
    public bool? BackupCatalogoRecente { get; init; }

    public IReadOnlyList<EspacoDisco>? Discos { get; init; }


    /// <summary>E-mails não enviados desde a volta anterior.</summary>
    public long? EmailsNaoEnviadosNoCiclo { get; init; }

    public IReadOnlySet<FonteAlerta> FontesAvaliadas
    {
        get
        {
            var fontes = new HashSet<FonteAlerta>();
            if (Worker is not null)
            {
                fontes.Add(FonteAlerta.Worker);
            }

            if (BackupCatalogoRecente is not null)
            {
                fontes.Add(FonteAlerta.Backup);
            }

            if (Discos is not null)
            {
                fontes.Add(FonteAlerta.Disco);
            }

            if (EmailsNaoEnviadosNoCiclo is not null)
            {
                fontes.Add(FonteAlerta.Email);
            }

            return fontes;
        }
    }
}

/// <summary>Um alerta ativo e a fonte que o produziu.</summary>
public sealed record AlertaAvaliado(FonteAlerta Fonte, AlertaOperacional Alerta)
{
    /// <summary>Alerta sobre o próprio e-mail não vai por e-mail (falharia de novo e voltaria a alertar).</summary>
    public bool EnviarPorEmail => Fonte != FonteAlerta.Email;

    /// <summary>Chave de notificação: o mesmo código com severidade MAIOR é notificado de novo (ex.: disco que piorou).</summary>
    public string Chave => $"{Alerta.Codigo}|{Alerta.Severidade}";
}

/// <summary>As regras de cada alerta, numa função pura (mesma entrada ⇒ mesmos alertas).</summary>
public static class AvaliacaoAlertas
{
    private const long Gb = 1024L * 1024 * 1024;

    public static IReadOnlyList<AlertaAvaliado> Avaliar(EntradasAlertas entradas, DateTimeOffset agoraUtc, OpcoesObservabilidade opcoes)
    {
        ArgumentNullException.ThrowIfNull(entradas);
        ArgumentNullException.ThrowIfNull(opcoes);
        var alertas = new List<AlertaAvaliado>();
        AvaliarWorker(entradas.Worker, alertas);
        AvaliarBackup(entradas.BackupCatalogoRecente, opcoes, alertas);
        AvaliarDiscos(entradas.Discos, opcoes, alertas);
        AvaliarEmail(entradas.EmailsNaoEnviadosNoCiclo, alertas);
        return alertas;
    }

    private static void AvaliarWorker(SituacaoServicoWorker? situacao, List<AlertaAvaliado> alertas)
    {
        if (situacao is null)
        {
            return;
        }

        if (situacao.ServicoForaDoAr)
        {
            alertas.Add(Novo(FonteAlerta.Worker, "WORKER-FORA-DO-AR", SeveridadeAlerta.Critico, "worker",
                situacao.Mensagem + " Inicie o serviço PortalBase.Worker (services.msc) e confira o Visualizador de Eventos."));
            return;
        }

        foreach (var nome in situacao.ComFalha)
        {
            alertas.Add(Novo(FonteAlerta.Worker, "WK-FALHA-" + nome, SeveridadeAlerta.Alto, "worker",
                $"O trabalhador '{nome}' do Worker terminou a última execução com erro. Veja Operações → Workers e o log do Worker."));
        }

        foreach (var nome in situacao.Travados)
        {
            alertas.Add(Novo(FonteAlerta.Worker, "WK-TRAVADO-" + nome, SeveridadeAlerta.Alto, "worker",
                $"O trabalhador '{nome}' do Worker parou de dar sinal (possível travamento). Reinicie o serviço PortalBase.Worker se persistir."));
        }
    }

    private static void AvaliarBackup(bool? recente, OpcoesObservabilidade opcoes, List<AlertaAvaliado> alertas)
    {
        if (recente is false)
        {
            alertas.Add(Novo(FonteAlerta.Backup, "BACKUP-ATRASADO", SeveridadeAlerta.Critico, "backup",
                string.Create(CultureInfo.InvariantCulture,
                    $"Nenhum backup do catálogo concluído nas últimas {Math.Max(1, opcoes.AlertaBackupHoras)} horas. Veja Operações → Backups e o log do Worker; rode um backup pela tela se preciso.")));
        }
    }

    private static void AvaliarDiscos(IReadOnlyList<EspacoDisco>? discos, OpcoesObservabilidade opcoes, List<AlertaAvaliado> alertas)
    {
        if (discos is null)
        {
            return;
        }

        var percentual = Math.Clamp(opcoes.DiscoLivreMinimoPercentual, 1, 50);
        var minimoBytes = Math.Clamp(opcoes.DiscoLivreMinimoGb, 0, 10_000) * Gb;
        foreach (var disco in discos)
        {
            SeveridadeAlerta? severidade =
                disco.PercentualLivre < percentual / 2.0 || disco.LivreBytes < minimoBytes / 2 ? SeveridadeAlerta.Critico
                : disco.PercentualLivre < percentual || disco.LivreBytes < minimoBytes ? SeveridadeAlerta.Alto
                : null;
            if (severidade is { } s)
            {
                alertas.Add(Novo(FonteAlerta.Disco, "DISCO-" + CodigoUnidade(disco.Unidade), s, "disco",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Disco {disco.Unidade} ({disco.Usos}) com {disco.PercentualLivre:0.#}% livre ({disco.LivreBytes / (double)Gb:0.#} GB). Libere espaço ou amplie o disco: sem espaço, backup, upload e log param.")));
            }
        }
    }

    private static void AvaliarEmail(long? naoEnviados, List<AlertaAvaliado> alertas)
    {
        if (naoEnviados is > 0)
        {
            alertas.Add(Novo(FonteAlerta.Email, "EMAIL-NAO-ENVIADO", SeveridadeAlerta.Alto, "email",
                string.Create(CultureInfo.InvariantCulture,
                    $"{naoEnviados} e-mail(s) não foram enviados desde a verificação anterior (servidor de e-mail com falha, fila cheia ou portal reiniciado com e-mails na fila). Confira Configurações → E-mail; quem pediu (ex.: redefinição de senha) precisa pedir de novo.")));
        }
    }

    private static AlertaAvaliado Novo(FonteAlerta fonte, string codigo, SeveridadeAlerta severidade, string componente, string descricao, Guid? tenant = null)
        => new(fonte, new AlertaOperacional(codigo, severidade, componente, descricao, tenant));

    /// <summary>Código curto e estável da unidade ("C:\" ⇒ "C"; "/" ⇒ "RAIZ"): só letras e dígitos.</summary>
    public static string CodigoUnidade(string unidade)
    {
        var limpo = new string((unidade ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Take(10).ToArray()).ToUpperInvariant();
        return limpo.Length == 0 ? "RAIZ" : limpo;
    }
}

/// <summary>
/// O que já foi notificado (singleton do processo): o alerta é disparado quando aparece, de novo a cada
/// <see cref="OpcoesObservabilidade.RenotificarAlerta"/> enquanto persistir, e quando a severidade sobe. Quando a
/// condição some (numa fonte que foi lida), o alerta "normaliza" e, se voltar, é disparado de novo. Se o processo
/// reiniciar, o alerta ativo é disparado uma vez mais (o incidente aberto só ganha uma nota).
/// </summary>
public sealed class EstadoAlertasNotificados
{
    private readonly Lock _trava = new();
    private readonly Dictionary<string, (FonteAlerta Fonte, string Codigo, DateTimeOffset NotificadoEm)> _notificados = new(StringComparer.Ordinal);
    private long? _ultimoTotalEmailsNaoEnviados;

    public bool DeveNotificar(AlertaAvaliado alerta, DateTimeOffset agoraUtc, TimeSpan renotificar)
    {
        ArgumentNullException.ThrowIfNull(alerta);
        lock (_trava)
        {
            if (_notificados.TryGetValue(alerta.Chave, out var anterior) && agoraUtc - anterior.NotificadoEm < renotificar)
            {
                return false;
            }

            _notificados[alerta.Chave] = (alerta.Fonte, alerta.Alerta.Codigo, agoraUtc);
            return true;
        }
    }

    /// <summary>Esquece os alertas que sumiram nas fontes avaliadas; devolve os códigos normalizados.</summary>
    public IReadOnlyList<string> Normalizar(IReadOnlyCollection<AlertaAvaliado> ativos, IReadOnlySet<FonteAlerta> fontesAvaliadas)
    {
        ArgumentNullException.ThrowIfNull(ativos);
        ArgumentNullException.ThrowIfNull(fontesAvaliadas);
        var chavesAtivas = ativos.Select(a => a.Chave).ToHashSet(StringComparer.Ordinal);
        lock (_trava)
        {
            var sumiram = _notificados
                .Where(par => fontesAvaliadas.Contains(par.Value.Fonte) && !chavesAtivas.Contains(par.Key))
                .ToList();
            foreach (var par in sumiram)
            {
                _notificados.Remove(par.Key);
            }

            return sumiram.Select(par => par.Value.Codigo).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Quantos e-mails deixaram de ser enviados desde a volta anterior. Na primeira volta do processo, os já contados
    /// também valem (o contador começa em zero quando o processo sobe).
    /// </summary>
    public long DiferencaEmailsNaoEnviados(long totalAtual)
    {
        lock (_trava)
        {
            var anterior = _ultimoTotalEmailsNaoEnviados ?? 0;
            _ultimoTotalEmailsNaoEnviados = totalAtual;
            return Math.Max(0, totalAtual - anterior);
        }
    }
}
