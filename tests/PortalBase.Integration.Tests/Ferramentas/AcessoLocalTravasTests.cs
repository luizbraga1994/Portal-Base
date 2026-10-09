using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.AcessoLocal;
using PortalBase.Domain.Catalogo;

namespace PortalBase.Integration.Tests.Ferramentas;

/// <summary>
/// Travas e validações do comando de acesso local que NÃO dependem de banco: ambiente (só Development, nunca
/// ambíguo), configuração (provedor local de segredos + MySQL desta máquina), argumentos (senha nunca por
/// argumento nem ecoada), política de senha e validação anterior a qualquer acesso ao catálogo.
/// </summary>
public sealed class AcessoLocalTravasTests
{
    // ---------------- ambiente ----------------

    [Theory]
    [InlineData("Development", null)]
    [InlineData(null, "Development")]
    [InlineData("Development", "Development")]
    public void Development_E_Aceito(string? dotnet, string? aspnet)
        => GuardaAmbienteLocal.VerificarAmbiente(dotnet, aspnet).Should().BeNull();

    [Theory]
    [InlineData(null, null)]
    [InlineData("Production", null)]
    [InlineData(null, "Production")]
    [InlineData("Staging", null)]
    [InlineData("development", null)] // comparação exata: variação de caixa não é "o mesmo" ambiente
    [InlineData("Development", "Production")] // ambíguo: recusa, nunca escolhe o mais permissivo
    [InlineData("Production", "Development")]
    public void Fora_De_Development_Ou_Ambiguo_E_Recusado(string? dotnet, string? aspnet)
        => GuardaAmbienteLocal.VerificarAmbiente(dotnet, aspnet).Should().StartWith("Recusado");

    [Theory]
    [InlineData("Development", "Production", "ASPNETCORE_ENVIRONMENT")] // o caso do launchSettings + variável do Windows
    [InlineData("Production", "Development", "DOTNET_ENVIRONMENT")]
    [InlineData(" Development ", "Production", "ASPNETCORE_ENVIRONMENT")]
    public void Ambiguo_Diz_Qual_Variavel_Remover(string dotnet, string aspnet, string divergente)
        => GuardaAmbienteLocal.VerificarAmbiente(dotnet, aspnet).Should()
            .StartWith("Recusado").And.Contain("ambíguo").And.Contain($"Remove-Item Env:{divergente};");

    [Theory]
    [InlineData("Production", null)]
    [InlineData(null, "Production")]
    [InlineData(null, null)]
    public void Fora_De_Development_Sem_Ambiguidade_Nao_Sugere_Remover_Variavel(string? dotnet, string? aspnet)
        => GuardaAmbienteLocal.VerificarAmbiente(dotnet, aspnet).Should().StartWith("Recusado").And.NotContain("Remove-Item");

    // ---------------- configuração ----------------

    [Theory]
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "[::1]")]
    [InlineData("LOCALHOST", "127.0.0.2")]
    public void Configuracao_Local_E_Aceita(string servidorCatalogo, string servidorTenant)
        => GuardaAmbienteLocal.VerificarConfiguracao(Configuracao(servidorCatalogo: servidorCatalogo, servidorTenant: servidorTenant))
            .Should().BeNull();

    [Fact]
    public void Cofre_Gerenciado_E_Recusado()
        => GuardaAmbienteLocal.VerificarConfiguracao(Configuracao(modoVault: "KeyVault")).Should().StartWith("Recusado");

    [Fact]
    public void Sem_Catalogo_Configurado_Orienta_Rodar_O_Script()
        => GuardaAmbienteLocal.VerificarConfiguracao(Configuracao(conexaoCatalogo: string.Empty))
            .Should().Contain("configurar-ambiente-local.ps1");

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("mysql.empresa.com.br")]
    [InlineData("localhost,10.0.0.5")] // lista de hosts: TODOS precisam ser locais
    [InlineData("0.0.0.0")]
    public void Catalogo_Fora_Desta_Maquina_E_Recusado(string servidor)
        => GuardaAmbienteLocal.VerificarConfiguracao(Configuracao(servidorCatalogo: servidor)).Should().StartWith("Recusado");

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("")]
    public void Base_Do_Contribuinte_Fora_Desta_Maquina_E_Recusada(string servidor)
        => GuardaAmbienteLocal.VerificarConfiguracao(Configuracao(servidorTenant: servidor)).Should().StartWith("Recusado");

    [Fact]
    public void Connection_String_Invalida_Nao_E_Ecoada()
    {
        var motivo = GuardaAmbienteLocal.VerificarConfiguracao(Configuracao(conexaoCatalogo: "server=localhost;password=Segredo-Que-Vaza;opcao_que_nao_existe=1"));

        motivo.Should().NotBeNull();
        motivo.Should().NotContain("Segredo-Que-Vaza");
    }

    // ---------------- argumentos ----------------

    [Theory]
    [InlineData("--senha", "Abc-Senha#2026")]
    [InlineData("--senha=Abc-Senha#2026")]
    [InlineData("--password", "Abc-Senha#2026")]
    [InlineData("-p", "Abc-Senha#2026")]
    public void Senha_Por_Argumento_E_Rejeitada_Sem_Ecoar_O_Valor(params string[] args)
    {
        var (argumentos, erro) = ArgumentosAcessoLocal.Interpretar(args);

        argumentos.Should().BeNull();
        erro.Should().Contain("não é aceita como argumento").And.NotContain("Abc-Senha#2026");
    }

    [Theory]
    [InlineData("Abc-Senha#2026")]
    [InlineData("--opcao-errada=Abc-Senha#2026")]
    public void Argumento_Desconhecido_Nao_Ecoa_Valores(string argumento)
    {
        var (_, erro) = ArgumentosAcessoLocal.Interpretar([argumento]);

        erro.Should().StartWith("Argumento desconhecido").And.NotContain("Abc-Senha#2026");
    }

    [Fact]
    public void Interpreta_Opcoes_Com_Espaco_E_Com_Igual_E_Papel_Padrao()
    {
        var (argumentos, erro) = ArgumentosAcessoLocal.Interpretar(
            ["--cnpj", "11.222.333/0001-81", "--tenant=stake", "--login=admin.local", "--razao-social", "Empresa Ltda", "--sem-mfa", "--REDEFINIR-SENHA"]);

        erro.Should().BeNull();
        argumentos!.Cnpj.Should().Be("11.222.333/0001-81");
        argumentos.Tenant.Should().Be("stake", "a normalização (MAIÚSCULO) e a validação são do domínio, na criação");
        argumentos.Login.Should().Be("admin.local");
        argumentos.RazaoSocial.Should().Be("Empresa Ltda");
        argumentos.ComMfa.Should().BeFalse("--sem-mfa é aceito por compatibilidade; sem MFA já é o padrão");
        argumentos.RemoverMfa.Should().BeFalse();
        argumentos.RedefinirSenha.Should().BeTrue();
        argumentos.Papeis.Should().Equal(PapelConhecido.Administrador);
    }

    [Fact]
    public void Papeis_Sao_Repetiveis_Sem_Duplicar()
    {
        var (argumentos, _) = ArgumentosAcessoLocal.Interpretar(["--papel", "leitor", "--papel=Analista", "--papel", "LEITOR"]);

        argumentos!.Papeis.Should().Equal(PapelConhecido.Leitor, PapelConhecido.Analista);
    }

    [Theory]
    [InlineData("3")] // número do enum não vira Administrador por acaso
    [InlineData("4")]
    [InlineData("Root")]
    [InlineData("Administrador,Leitor")]
    [InlineData("Leitor,Analista")] // regressão: combinação de bits (1|2 = 3) viraria Administrador
    public void Papel_Desconhecido_E_Rejeitado(string papel)
        => ArgumentosAcessoLocal.Interpretar(["--papel", papel]).Erro.Should().StartWith("Papel desconhecido");

    [Theory]
    [InlineData("--cnpj")]
    [InlineData("--cnpj", "--login", "x")]
    [InlineData("--cnpj=")]
    [InlineData("--login", "a", "--login", "b")]
    [InlineData("--senha-stdin", "--com-mfa")] // stdin redirecionado é incompatível com o cadastro interativo do MFA
    [InlineData("--com-mfa", "--remover-mfa")] // opostos
    public void Uso_Incorreto_E_Rejeitado(params string[] args)
        => ArgumentosAcessoLocal.Interpretar(args).Argumentos.Should().BeNull();

    [Fact]
    public void Mfa_E_Opcional_Por_Padrao_E_So_Entra_Com_Com_Mfa()
    {
        var (padrao, _) = ArgumentosAcessoLocal.Interpretar(["--cnpj", "11222333000181", "--login", "admin.local"]);
        padrao!.ComMfa.Should().BeFalse();
        padrao.RemoverMfa.Should().BeFalse();

        ArgumentosAcessoLocal.Interpretar(["--com-mfa"]).Argumentos!.ComMfa.Should().BeTrue();
        ArgumentosAcessoLocal.Interpretar(["--remover-mfa"]).Argumentos!.RemoverMfa.Should().BeTrue();
    }

    [Theory]
    [InlineData(new string[0], false, false)] // padrão: sem MFA, sem perguntar
    [InlineData(new[] { "--sem-mfa" }, false, false)]
    [InlineData(new[] { "--com-mfa" }, true, false)]
    [InlineData(new[] { "--remover-mfa" }, false, true)]
    public void Linha_De_Comando_Vira_Entrada_Com_Mfa_Opcional(string[] opcoesMfa, bool ativar, bool remover)
    {
        var (argumentos, _) = ArgumentosAcessoLocal.Interpretar(["--cnpj", "11222333000181", "--login", "admin.local", .. opcoesMfa]);

        var entrada = ProgramaAcessoLocal.Completar(argumentos!, new TerminalFalso());

        entrada!.AtivarMfa.Should().Be(ativar);
        entrada.RemoverMfa.Should().Be(remover);
    }

    [Fact]
    public void Senha_Stdin_Base64_Implica_Stdin_E_Nao_Combina_Com_Cadastro_Do_Mfa()
    {
        var (argumentos, _) = ArgumentosAcessoLocal.Interpretar(["--senha-stdin-base64"]);
        argumentos!.SenhaStdin.Should().BeTrue();
        argumentos.SenhaStdinBase64.Should().BeTrue();

        ArgumentosAcessoLocal.Interpretar(["--senha-stdin-base64", "--com-mfa"]).Argumentos.Should().BeNull();
    }

    [Fact]
    public async Task Cadastrar_E_Remover_Mfa_Juntos_E_Rejeitado_Antes_De_Abrir_O_Catalogo()
    {
        var criador = new CriadorAcessoLocal(new EscoposProibidos(), new TerminalFalso(), "acesso-local:teste");

        var resultado = await criador.ExecutarAsync(
            new EntradaAcessoLocal("11222333000181", "admin.local", [PapelConhecido.Administrador]) { AtivarMfa = true, RemoverMfa = true });

        resultado.Should().Be(ResultadoAcessoLocal.EntradaInvalida);
    }

    [Theory]
    [InlineData("Pipe-Usuário#2026é")] // acentos: o motivo do Base64 (o pipe do Windows PowerShell 5.1 é ASCII)
    [InlineData("Senha-Local#2026")]
    public void Senha_Base64_Volta_Exatamente_Ao_Texto_Utf8(string senha)
        => ProgramaAcessoLocal.DecodificarSenhaBase64(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(senha))).Should().Be(senha);

    [Theory]
    [InlineData("")]
    [InlineData("não é base64!")]
    [InlineData("ww==")] // byte 0xC3 sozinho: UTF-8 inválido é rejeitado, nunca vira '?'
    public void Senha_Base64_Invalida_E_Rejeitada(string linha)
        => ProgramaAcessoLocal.DecodificarSenhaBase64(linha).Should().BeNull();

    [Fact]
    public async Task Programa_Recusa_Base64_Invalido_Sem_Ecoar_A_Linha()
    {
        var terminal = new TerminalFalso(interativo: false) { LinhaEntrada = "Segredo-Nao-Base64#2026" };

        var codigo = await ProgramaAcessoLocal.ExecutarAsync(
            ["--cnpj", "11222333000181", "--login", "admin.local", "--sem-mfa", "--senha-stdin-base64"],
            terminal, Variaveis(("DOTNET_ENVIRONMENT", "Development")), () => Configuracao(), "teste", CancellationToken.None);

        codigo.Should().Be((int)ResultadoAcessoLocal.EntradaInvalida);
        terminal.Texto.Should().Contain("Base64").And.NotContain("Segredo-Nao-Base64#2026");
    }

    // ---------------- política de senha ----------------

    // ---------------- validação anterior ao banco ----------------

    [Theory]
    [InlineData("11.222.333/0001-00", "admin.local", false)] // DV errado
    [InlineData("11111111111111", "admin.local", false)]
    [InlineData("11222333000181", "ad", false)] // login curto
    [InlineData("11222333000181", "admin local", false)] // espaço
    [InlineData("11222333000181", "admin'--", false)]
    [InlineData("11222333000181", "admin.local", true)] // MFA pedido sem terminal interativo
    public async Task Entrada_Invalida_E_Rejeitada_Antes_De_Abrir_O_Catalogo(string cnpj, string login, bool ativarMfa)
    {
        var terminal = new TerminalFalso(interativo: false);
        var criador = new CriadorAcessoLocal(new EscoposProibidos(), terminal, "acesso-local:teste");

        var resultado = await criador.ExecutarAsync(new EntradaAcessoLocal(cnpj, login, [PapelConhecido.Administrador]) { AtivarMfa = ativarMfa });

        resultado.Should().Be(ResultadoAcessoLocal.EntradaInvalida, terminal.Texto);
    }

    [Fact]
    public async Task Sem_Papel_E_Rejeitado_Antes_De_Abrir_O_Catalogo()
    {
        var criador = new CriadorAcessoLocal(new EscoposProibidos(), new TerminalFalso(interativo: false), "acesso-local:teste");

        var resultado = await criador.ExecutarAsync(new EntradaAcessoLocal("11222333000181", "admin.local", []) { AtivarMfa = false });

        resultado.Should().Be(ResultadoAcessoLocal.EntradaInvalida);
    }

    [Theory]
    [InlineData("joao", "acesso-local:joao")]
    [InlineData("DOMINIO\\maria silva", "acesso-local:DOMINIOmariasilva")]
    [InlineData("x\r\nPrincipalId=admin", "acesso-local:xPrincipalIdadmin")] // sem quebra de linha/injeção no ator
    [InlineData("", "acesso-local:desconhecido")]
    [InlineData(null, "acesso-local:desconhecido")]
    public void Ator_De_Auditoria_So_Tem_Caracteres_Seguros(string? usuarioSistema, string esperado)
        => CriadorAcessoLocal.AtorPara(usuarioSistema).Should().Be(esperado);

    // ---------------- programa (ordem das travas) ----------------

    [Fact]
    public async Task Programa_Recusa_Fora_De_Development_Antes_De_Ler_Qualquer_Configuracao()
    {
        var terminal = new TerminalFalso(interativo: false);
        var configuracaoLida = false;

        var codigo = await ProgramaAcessoLocal.ExecutarAsync(
            ["--cnpj", "11222333000181", "--login", "admin.local", "--sem-mfa"],
            terminal,
            Variaveis(("DOTNET_ENVIRONMENT", "Production")),
            () =>
            {
                configuracaoLida = true;
                return Configuracao();
            },
            "teste",
            CancellationToken.None);

        codigo.Should().Be((int)ResultadoAcessoLocal.Recusado);
        configuracaoLida.Should().BeFalse("nem a configuração (com os segredos locais) é lida fora de Development");
        terminal.Texto.Should().Contain("só roda em Development");
    }

    [Fact]
    public async Task Programa_Recusa_Configuracao_Que_Nao_E_Local_Mesmo_Em_Development()
    {
        var terminal = new TerminalFalso(interativo: false);

        var codigo = await ProgramaAcessoLocal.ExecutarAsync(
            ["--cnpj", "11222333000181", "--login", "admin.local", "--sem-mfa"],
            terminal,
            Variaveis(("DOTNET_ENVIRONMENT", "Development")),
            () => Configuracao(modoVault: "KeyVault"),
            "teste",
            CancellationToken.None);

        codigo.Should().Be((int)ResultadoAcessoLocal.Recusado);
        terminal.Texto.Should().Contain("Vault:Modo");
    }

    [Fact]
    public async Task Programa_Ajuda_Explica_Que_A_Senha_Nao_Vai_Por_Argumento()
    {
        var terminal = new TerminalFalso(interativo: false);

        var codigo = await ProgramaAcessoLocal.ExecutarAsync(
            ["--ajuda"], terminal, _ => null, () => throw new InvalidOperationException("não lê configuração"), "teste", CancellationToken.None);

        codigo.Should().Be((int)ResultadoAcessoLocal.Sucesso);
        terminal.Texto.Should().Contain("--senha-stdin").And.Contain("nunca é aceita como argumento");
    }

    [Fact]
    public async Task Programa_Le_A_Senha_Do_Stdin_Somente_Sem_Terminal_Interativo()
    {
        var interativo = new TerminalFalso(interativo: true);

        var codigo = await ProgramaAcessoLocal.ExecutarAsync(
            ["--cnpj", "11222333000181", "--login", "admin.local", "--sem-mfa", "--senha-stdin"],
            interativo, Variaveis(("DOTNET_ENVIRONMENT", "Development")), () => Configuracao(), "teste", CancellationToken.None);

        codigo.Should().Be((int)ResultadoAcessoLocal.EntradaInvalida);
        interativo.Texto.Should().Contain("--senha-stdin exige");
    }

    [Fact]
    public void Configuracao_Do_Comando_Sobrepoe_Development_Ao_Arquivo_Base_Como_A_Web()
    {
        var diretorio = Directory.CreateTempSubdirectory("acesso-local-");
        try
        {
            File.WriteAllText(Path.Combine(diretorio.FullName, "appsettings.json"), """{ "TesteAcessoLocal": { "A": "base", "B": "base" } }""");
            File.WriteAllText(Path.Combine(diretorio.FullName, "appsettings.Development.json"), """{ "TesteAcessoLocal": { "B": "dev" } }""");

            var configuracao = ProgramaAcessoLocal.CarregarConfiguracao(diretorio.FullName);

            configuracao["TesteAcessoLocal:A"].Should().Be("base");
            configuracao["TesteAcessoLocal:B"].Should().Be("dev");
        }
        finally
        {
            diretorio.Delete(recursive: true);
        }
    }

    [Fact]
    public void Configuracao_Base_E_User_Secrets_Do_Comando_Sao_Os_Da_Web()
    {
        // O comando lê EXATAMENTE a configuração da Web: os mesmos appsettings (vinculados no csproj, não copiados
        // à mão) e o mesmo UserSecretsId que o configurar-ambiente-local.ps1 preenche.
        var raiz = new DirectoryInfo(AppContext.BaseDirectory);
        while (raiz is not null && !File.Exists(Path.Combine(raiz.FullName, "PortalBase.sln")))
        {
            raiz = raiz.Parent;
        }

        raiz.Should().NotBeNull();
        var csproj = File.ReadAllText(Path.Combine(raiz!.FullName, "tools", "PortalBase.AcessoLocal", "PortalBase.AcessoLocal.csproj"));
        csproj.Should().Contain(@"Include=""..\..\src\PortalBase.Web\appsettings.json""")
            .And.Contain(@"Include=""..\..\src\PortalBase.Web\appsettings.Development.json""");

        var idWeb = typeof(PortalBase.Web.Components.App).Assembly
            .GetCustomAttribute<Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute>()!.UserSecretsId;
        ProgramaAcessoLocal.UserSecretsIdWeb.Should().Be(idWeb);
    }

    private static Func<string, string?> Variaveis(params (string Nome, string Valor)[] valores)
        => nome => valores.FirstOrDefault(v => v.Nome == nome).Valor;

    private static IConfiguration Configuracao(
        string modoVault = "ConfiguracaoLocal",
        string? conexaoCatalogo = null,
        string servidorCatalogo = "localhost",
        string servidorTenant = "localhost")
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Modo"] = modoVault,
                ["ConnectionStrings:Catalogo"] = conexaoCatalogo ?? $"server={servidorCatalogo};port=3306;database=portalbase_catalogo;user id=portalbase_catalogo",
                ["BancoTenant:Servidor"] = servidorTenant,
            })
            .Build();

    /// <summary>Prova de que a validação acontece ANTES de qualquer escopo (catálogo/DI): abrir escopo falha o teste.</summary>
    private sealed class EscoposProibidos : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("A validação deveria ter recusado antes de abrir o catálogo.");
    }
}
