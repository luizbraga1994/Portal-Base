using System.Text;
using PortalBase.Application.Identidade;

namespace PortalBase.Web.Hospedagem;

/// <summary>
/// Comando de implantação <c>PortalBase.Web.exe criar-primeiro-administrador</c>: cria o PRIMEIRO Administrador da
/// plataforma de um catálogo novo e sai, sem subir o site. Roda no servidor, com a mesma configuração do site (o
/// <c>scripts/configurar-iis.ps1</c> chama com as variáveis do pool). Só num console interativo; a senha é digitada sem
/// eco e nunca é escrita. Depois do primeiro, o comando se recusa (novos administradores só pela tela).
///
/// É a única exceção ao banimento de <c>Console.Write*</c> na Web (regras 8/16): aqui o console É a interface de quem
/// administra o servidor; nada de segredo ou detalhe interno é escrito.
/// </summary>
internal static class ComandoPrimeiroAdministrador
{
    public const string Nome = "criar-primeiro-administrador";

    public static bool Solicitado(string[] args) => args.Length > 0 && string.Equals(args[0], Nome, StringComparison.OrdinalIgnoreCase);

#pragma warning disable RS0030 // Comando interativo de implantação: o console é a interface (ver resumo da classe).
    public static async Task<int> ExecutarAsync(IServiceProvider servicos)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Rode este comando num console interativo (a senha é digitada sem aparecer na tela).");
            return 2;
        }

        try
        {
            using var escopo = servicos.CreateScope();
            var comando = escopo.ServiceProvider.GetRequiredService<PrimeiroAdministradorPlataforma>();
            if (await comando.JaExisteAsync().ConfigureAwait(false))
            {
                Console.WriteLine("Já existe Administrador da plataforma. Novos administradores são concedidos pela tela (Administração → Usuários).");
                return 1;
            }

            Console.WriteLine("Primeiro Administrador da plataforma (acesso total ao portal).");
            Console.Write("Nome: ");
            var nome = Console.ReadLine();
            Console.Write("E-mail (é o login): ");
            var email = Console.ReadLine();
            var senha = LerOculto("Senha (mín. 12 caracteres; 3 tipos entre minúsculas, maiúsculas, dígitos e símbolos)");
            if (!string.Equals(senha, LerOculto("Repita a senha"), StringComparison.Ordinal))
            {
                Console.Error.WriteLine("As senhas digitadas são diferentes. Nada foi gravado.");
                return 1;
            }

            var executor = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var r = await comando.CriarAsync(nome, email, senha, executor).ConfigureAwait(false);
            (r.Sucesso ? Console.Out : Console.Error).WriteLine(r.Mensagem);
            return r.Sucesso ? 0 : 1;
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            // sem-log: comando de console (o host não está no ar); a saída de erro do terminal é o registro. Sem detalhe
            // interno (connection string, pilha): só o caminho para resolver e o TIPO da falha, para o suporte.
            Console.Error.WriteLine(
                "Não foi possível acessar o banco de catálogo ou o cofre. Confira ConnectionStrings:Catalogo, BancoCatalogo:SenhaRef, " +
                $"a pasta do cofre e se as migrations do catálogo foram aplicadas. (Tipo da falha: {excecao.GetType().Name}.)");
            return 3;
        }
    }

    internal static string LerOculto(string rotulo)
    {
        Console.Write(rotulo + ": ");
        var valor = new StringBuilder();
        while (true)
        {
            var tecla = Console.ReadKey(intercept: true);
            if (tecla.Key == ConsoleKey.Enter)
            {
                Console.WriteLine(string.Empty);
                return valor.ToString();
            }

            if (tecla.Key == ConsoleKey.Backspace)
            {
                if (valor.Length > 0)
                {
                    valor.Length--;
                }

                continue;
            }

            if (!char.IsControl(tecla.KeyChar))
            {
                valor.Append(tecla.KeyChar);
            }
        }
    }
#pragma warning restore RS0030
}
