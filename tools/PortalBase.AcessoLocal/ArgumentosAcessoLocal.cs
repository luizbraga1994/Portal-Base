using PortalBase.Domain.Catalogo;

namespace PortalBase.AcessoLocal;

/// <summary>
/// Linha de comando do <c>PortalBase.AcessoLocal</c>. Não existe opção de senha por argumento: ela ficaria no
/// histórico do shell e na lista de processos (regra 7) — a senha é digitada sem eco ou chega por
/// <c>--senha-stdin</c>.
/// </summary>
public sealed class ArgumentosAcessoLocal
{
    public const string Uso = """
        Cria o primeiro acesso LOCAL (somente Development): contribuinte provisionado e ativo, usuário com senha,
        vínculo e papéis; MFA é OPCIONAL. Idempotente: rodar de novo completa o que faltar e não duplica nada.

        Uso: dotnet run --project tools/PortalBase.AcessoLocal -- [opções]
             (ou .\scripts\criar-acesso-local.ps1 com os mesmos argumentos)

          --cnpj <14 dígitos>       CNPJ do contribuinte (com ou sem máscara)
          --tenant <NOME>           nome do tenant, ex.: STAKE (vira a base portalbase_tenant_stake; usado
                                    só ao criar o contribuinte; sempre em maiúsculas; letras, dígitos, _)
          --razao-social <texto>    razão social (usada só ao criar o contribuinte)
          --login <usuário>         nome de usuário interno (3 a 64: letras, dígitos, . _ - @); identifica o
                                    usuário neste comando
          --nome <texto>            nome do usuário (usado só ao criar o usuário)
          --email <e-mail>          e-mail do usuário: é com ELE que se entra no portal (gravado ao criar o
                                    usuário; único — não pode ser o de outro usuário)
          --papel <papel>           Leitor | Analista | Administrador (repetível;
                                    padrão: Administrador)
          --redefinir-senha         troca a senha de um usuário que já existe
          --com-mfa                 cadastra o MFA (TOTP) agora, no terminal (opcional; padrão: sem MFA)
          --remover-mfa             desliga o MFA de um usuário que já o tem (o login volta a pedir só a senha)
          --sem-mfa                 o padrão (aceito por compatibilidade)
          --senha-stdin             lê a senha da 1ª linha da entrada padrão (automação; não combina com --com-mfa)
          --senha-stdin-base64      idem, com a linha em Base64 dos bytes UTF-8 da senha (usado pelo .ps1:
                                    só ASCII no pipe, imune à página de código do console)
          --ajuda                   mostra esta ajuda

        O que faltar é perguntado no terminal. A senha é digitada sem eco e nunca é aceita como argumento.
        """;

    public string? Cnpj { get; private set; }

    public string? Tenant { get; private set; }

    public string? RazaoSocial { get; private set; }

    public string? Login { get; private set; }

    public string? Nome { get; private set; }

    public string? Email { get; private set; }

    public IReadOnlyList<PapelConhecido> Papeis => _papeis.Count == 0 ? [PapelConhecido.Administrador] : _papeis;

    public bool RedefinirSenha { get; private set; }

    /// <summary>Cadastrar o MFA agora (opt-in). Sem isto, o usuário fica sem MFA — o login pede só a senha.</summary>
    public bool ComMfa { get; private set; }

    /// <summary>Desligar o MFA de um usuário existente.</summary>
    public bool RemoverMfa { get; private set; }

    public bool SenhaStdin { get; private set; }

    /// <summary>A linha do stdin vem em Base64 (UTF-8): nenhum byte não ASCII passa pelo pipe do shell.</summary>
    public bool SenhaStdinBase64 { get; private set; }

    public bool Ajuda { get; private set; }

    private readonly List<PapelConhecido> _papeis = [];

    /// <summary>Interpreta os argumentos; devolve o motivo da rejeição em <c>Erro</c> (e <c>Argumentos</c> nulo).</summary>
    public static (ArgumentosAcessoLocal? Argumentos, string? Erro) Interpretar(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var resultado = new ArgumentosAcessoLocal();
        for (var i = 0; i < args.Count; i++)
        {
            var (nome, valorEmbutido) = Separar(args[i]);

            if (nome is "--senha" or "--password" or "-p")
            {
                return (null, "A senha não é aceita como argumento (ficaria no histórico do shell). "
                    + "Omita-a para digitá-la sem eco, ou use --senha-stdin.");
            }

            switch (nome)
            {
                case "--ajuda" or "--help" or "-h" or "-?":
                    resultado.Ajuda = true;
                    continue;
                case "--redefinir-senha":
                    resultado.RedefinirSenha = true;
                    continue;
                case "--sem-mfa":
                    continue; // já é o padrão
                case "--com-mfa":
                    resultado.ComMfa = true;
                    continue;
                case "--remover-mfa":
                    resultado.RemoverMfa = true;
                    continue;
                case "--senha-stdin":
                    resultado.SenhaStdin = true;
                    continue;
                case "--senha-stdin-base64":
                    resultado.SenhaStdin = true;
                    resultado.SenhaStdinBase64 = true;
                    continue;
                case "--cnpj" or "--tenant" or "--razao-social" or "--login" or "--nome" or "--email" or "--papel":
                    break;
                default:
                    return (null, $"Argumento desconhecido: {Descrever(nome)}. Use --ajuda.");
            }

            string? valor = valorEmbutido;
            if (valor is null)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return (null, $"A opção {nome} exige um valor.");
                }

                valor = args[++i];
            }

            if (string.IsNullOrWhiteSpace(valor))
            {
                return (null, $"A opção {nome} exige um valor não vazio.");
            }

            var erro = resultado.Atribuir(nome, valor.Trim());
            if (erro is not null)
            {
                return (null, erro);
            }
        }

        if (resultado.ComMfa && resultado.RemoverMfa)
        {
            return (null, "--com-mfa e --remover-mfa são opostos: use só um.");
        }

        if (resultado.SenhaStdin && resultado.ComMfa)
        {
            return (null, "--senha-stdin redireciona a entrada padrão, e cadastrar o MFA exige terminal interativo: sem --com-mfa nesse caso.");
        }

        return (resultado, null);
    }

    private string? Atribuir(string nome, string valor)
    {
        if (nome == "--papel")
        {
            // Só o NOME exato de um papel (sem diferenciar maiúsculas). Nunca Enum.TryParse: ele aceita números ("3") e
            // listas com vírgula como combinação de bits ("Leitor,Analista" = 1|2 = 3 = Administrador) — escalada de papel.
            var nomePapel = Enum.GetNames<PapelConhecido>().FirstOrDefault(n => string.Equals(n, valor, StringComparison.OrdinalIgnoreCase));
            if (nomePapel is null)
            {
                return $"Papel desconhecido. Use: {string.Join(", ", Enum.GetNames<PapelConhecido>())}.";
            }

            var papel = Enum.Parse<PapelConhecido>(nomePapel);

            if (!_papeis.Contains(papel))
            {
                _papeis.Add(papel);
            }

            return null;
        }

        var anterior = nome switch
        {
            "--cnpj" => Cnpj,
            "--tenant" => Tenant,
            "--razao-social" => RazaoSocial,
            "--login" => Login,
            "--nome" => Nome,
            _ => Email,
        };
        if (anterior is not null)
        {
            return $"A opção {nome} foi informada mais de uma vez.";
        }

        switch (nome)
        {
            case "--cnpj":
                Cnpj = valor;
                break;
            case "--tenant":
                Tenant = valor;
                break;
            case "--razao-social":
                RazaoSocial = valor;
                break;
            case "--login":
                Login = valor;
                break;
            case "--nome":
                Nome = valor;
                break;
            default:
                Email = valor;
                break;
        }

        return null;
    }

    private static (string Nome, string? Valor) Separar(string argumento)
    {
        if (argumento.StartsWith("--", StringComparison.Ordinal))
        {
            var igual = argumento.IndexOf('=', StringComparison.Ordinal);
            if (igual > 0)
            {
                return (argumento[..igual].ToLowerInvariant(), argumento[(igual + 1)..]);
            }
        }

        return (argumento.ToLowerInvariant(), null);
    }

    // Um valor solto pode ser uma senha colada por engano: só NOMES de opção são ecoados (sem o "=valor").
    private static string Descrever(string nome) =>
        nome.StartsWith('-') && nome.Length <= 40 ? $"'{nome}'" : "valor solto (não exibido)";
}
