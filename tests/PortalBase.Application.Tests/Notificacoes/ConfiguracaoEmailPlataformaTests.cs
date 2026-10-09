using FluentAssertions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Excecoes;
using PortalBase.Application.Notificacoes;
using PortalBase.Application.Tests.Dobras;

namespace PortalBase.Application.Tests.Notificacoes;

/// <summary>
/// Configurações → E-mail: só o Administrador da plataforma; validação antes de gravar; a senha vai SÓ para o cofre (nunca
/// para o catálogo, a tela ou a auditoria); antes/depois auditado; teste de envio auditado sem o destinatário.
/// </summary>
public sealed class ConfiguracaoEmailPlataformaTests
{
    private static readonly AlteracaoConfiguracaoEmail Valida =
        new(true, "smtp.exemplo.com", 587, "StartTls", "portal", "Senha#Smtp1", "nao-responda@exemplo.com", "Portal Base", "https://portal.exemplo.com");

    [Fact]
    public async Task Salva_Senha_So_No_Cofre_E_Audita_Sem_Ela()
    {
        var c = new Cenario();

        var r = await c.Comando.SalvarAsync(Valida);

        r.Sucesso.Should().BeTrue();
        c.Cofre.Definidos.Should().Equal([("ref-smtp", "Senha#Smtp1")]);
        c.Portal.Salvas.Should().ContainSingle().Which.NovaSenha.Should().BeNull("a senha não vai para o catálogo");
        var evento = c.Auditoria.Eventos.Should().ContainSingle().Subject;
        evento.Operacao.Should().Be(AcaoAuditoria.EmailConfigurationUpdated);
        evento.Tenant.Should().Be("GLOBAL");
        evento.ValoresNovos!["senha"].Should().Be("alterada");
        evento.ValoresNovos["servidor"].Should().Be("smtp.exemplo.com");
        string.Join('|', evento.ValoresAnteriores!.Values.Concat(evento.ValoresNovos.Values)).Should().NotContain("Senha#Smtp1");
    }

    [Fact]
    public async Task Sem_Nova_Senha_Mantem_A_Do_Cofre()
    {
        var c = new Cenario();

        (await c.Comando.SalvarAsync(Valida with { NovaSenha = "" })).Sucesso.Should().BeTrue();

        c.Cofre.Definidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalida_Nao_Grava_Nada()
    {
        var c = new Cenario();
        c.Portal.Erros = ["Servidor deve ser um nome de servidor ou IP."];

        var r = await c.Comando.SalvarAsync(Valida);

        r.Sucesso.Should().BeFalse();
        r.Erros.Should().ContainSingle();
        c.Cofre.Definidos.Should().BeEmpty();
        c.Portal.Salvas.Should().BeEmpty();
        c.Auditoria.Eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task Sem_Permissao_De_Plataforma_Nega_Tudo()
    {
        var c = new Cenario();
        c.Plataforma.Permitir = false;

        await ((Func<Task>)(() => c.Comando.ObterAsync())).Should().ThrowAsync<AutorizacaoNegadaException>();
        await ((Func<Task>)(() => c.Comando.SalvarAsync(Valida))).Should().ThrowAsync<AutorizacaoNegadaException>();
        await ((Func<Task>)(() => c.Comando.TestarAsync("a@b.com"))).Should().ThrowAsync<AutorizacaoNegadaException>();
        await ((Func<Task>)(() => c.Comando.RestaurarConfiguracaoDoServidorAsync())).Should().ThrowAsync<AutorizacaoNegadaException>();
        await ((Func<Task>)(() => c.Comando.SalvarAparenciaAsync(new AlteracaoAparenciaEmail("Azul", null, null)))).Should().ThrowAsync<AutorizacaoNegadaException>();
        await ((Func<Task>)(() => c.Comando.PreviaAsync(new AlteracaoAparenciaEmail("Azul", null, null)))).Should().ThrowAsync<AutorizacaoNegadaException>();
        c.Portal.Aparencias.Should().BeEmpty();
        c.Cofre.Definidos.Should().BeEmpty();
        c.Portal.Testes.Should().BeEmpty();
    }

    [Fact]
    public async Task Teste_Envia_E_Audita_So_O_Dominio_Do_Destinatario()
    {
        var c = new Cenario();

        var r = await c.Comando.TestarAsync(" maria@exemplo.com ");

        r.Sucesso.Should().BeTrue();
        c.Portal.Testes.Should().Equal("maria@exemplo.com");
        var evento = c.Auditoria.Eventos.Should().ContainSingle().Subject;
        evento.Operacao.Should().Be(AcaoAuditoria.EmailTestSent);
        evento.Motivo.Should().Contain("exemplo.com").And.NotContain("maria");
    }

    [Fact]
    public async Task Teste_Com_Falha_Devolve_O_Motivo_E_Audita_Como_Negado()
    {
        var c = new Cenario();
        c.Portal.FalhaTeste = "O servidor recusou o usuário ou a senha do SMTP.";

        var r = await c.Comando.TestarAsync("maria@exemplo.com");

        r.Sucesso.Should().BeFalse();
        r.Erros.Should().Equal("O servidor recusou o usuário ou a senha do SMTP.");
        c.Auditoria.Eventos.Should().ContainSingle(e => e.Resultado == ResultadoAuditoria.Negado);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nao-e-email")]
    public async Task Teste_Com_Endereco_Invalido_Nao_Envia(string? para)
    {
        var c = new Cenario();

        (await c.Comando.TestarAsync(para)).Sucesso.Should().BeFalse();
        c.Portal.Testes.Should().BeEmpty();
    }

    [Fact]
    public async Task Teste_Com_Envio_Desligado_Nao_Envia()
    {
        var c = new Cenario();
        c.Portal.Atual = c.Portal.Atual with { Habilitado = false, Modo = "Desligado" };

        (await c.Comando.TestarAsync("maria@exemplo.com")).Erros.Should().ContainSingle(e => e.Contains("desligado"));
        c.Portal.Testes.Should().BeEmpty();
    }

    [Fact]
    public async Task Restaurar_Remove_A_Da_Tela_E_Audita()
    {
        var c = new Cenario();

        await c.Comando.RestaurarConfiguracaoDoServidorAsync();

        c.Portal.Removida.Should().BeTrue();
        c.Auditoria.Eventos.Should().ContainSingle(e => e.Operacao == AcaoAuditoria.EmailConfigurationUpdated && e.Motivo!.Contains("servidor"));
    }

    [Fact]
    public async Task Aparencia_Salva_E_Audita_Antes_E_Depois()
    {
        var c = new Cenario();
        var nova = new AlteracaoAparenciaEmail("Carmim", "#C0142A", "Andersen Consulting · (11) 0000-0000");
        c.Portal.AoSalvarAparencia = a => c.Portal.Atual = c.Portal.Atual with { Tema = a.Tema!, CorDestaque = a.CorDestaque!, TextoRodape = a.TextoRodape! };

        var r = await c.Comando.SalvarAparenciaAsync(nova);

        r.Sucesso.Should().BeTrue();
        c.Portal.Aparencias.Should().Equal(nova);
        var evento = c.Auditoria.Eventos.Should().ContainSingle().Subject;
        evento.Operacao.Should().Be(AcaoAuditoria.EmailConfigurationUpdated);
        evento.ValoresAnteriores!["tema"].Should().Be("Portal");
        evento.ValoresNovos!["tema"].Should().Be("Carmim");
        evento.ValoresNovos["corDestaque"].Should().Be("#C0142A");
    }

    [Fact]
    public async Task Aparencia_Invalida_Nao_Grava()
    {
        var c = new Cenario();
        c.Portal.ErrosAparencia = ["A cor de destaque deve estar no formato #RRGGBB (ex.: #9E1B32)."];

        var r = await c.Comando.SalvarAparenciaAsync(new AlteracaoAparenciaEmail("Portal", "vermelho", null));

        r.Sucesso.Should().BeFalse();
        r.Erros.Should().ContainSingle(e => e.Contains("#RRGGBB"));
        c.Portal.Aparencias.Should().BeEmpty();
        c.Auditoria.Eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task Aparencia_Com_A_Configuracao_Do_Servidor_Pede_Para_Salvar_O_Envio_Antes()
    {
        var c = new Cenario();
        c.Portal.Atual = c.Portal.Atual with { Origem = OrigemConfiguracaoEmail.Servidor };

        var r = await c.Comando.SalvarAparenciaAsync(new AlteracaoAparenciaEmail("Azul", null, null));

        r.Sucesso.Should().BeFalse();
        r.Erros.Should().ContainSingle(e => e.Contains("Salve primeiro o envio"));
        c.Portal.Aparencias.Should().BeEmpty();
    }

    [Fact]
    public async Task Previa_Usa_O_Endereco_Do_Portal_Em_Vigor()
    {
        var c = new Cenario();

        var html = await c.Comando.PreviaAsync(new AlteracaoAparenciaEmail("Azul", null, null));

        html.Should().Be("previa:Azul:https://portal.exemplo.com");
    }

    private sealed class Cenario
    {
        public Cenario()
            => Comando = new ConfiguracaoEmailPlataforma(Plataforma, new ContextoSegurancaFake { PrincipalId = Guid.NewGuid().ToString() }, Portal, Cofre, Auditoria,
                new RelogioFixo(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)));

        public AutorizacaoPlataformaFake Plataforma { get; } = new();

        public PortalFake Portal { get; } = new();

        public CofreFake Cofre { get; } = new();

        public AuditoriaFake Auditoria { get; } = new();

        public ConfiguracaoEmailPlataforma Comando { get; }
    }

    private sealed class PortalFake : IConfiguracaoEmailPortal
    {
        public ConfiguracaoEmailAtual Atual { get; set; } = new(
            OrigemConfiguracaoEmail.Tela, "Smtp", true, "smtp.exemplo.com", 587, "StartTls", "portal", true, "nao-responda@exemplo.com", "Portal Base", "https://portal.exemplo.com", null);

        public IReadOnlyList<string> Erros { get; set; } = [];

        public List<AlteracaoConfiguracaoEmail> Salvas { get; } = [];

        public List<string> Testes { get; } = [];

        public string? FalhaTeste { get; set; }

        public bool Removida { get; private set; }

        public string NomeSegredoSenha => "ref-smtp";

        public Task<ConfiguracaoEmailAtual> ObterAsync(CancellationToken cancellationToken = default) => Task.FromResult(Atual);

        public IReadOnlyList<string> Validar(AlteracaoConfiguracaoEmail alteracao, bool senhaDefinida) => Erros;

        public Task SalvarAsync(AlteracaoConfiguracaoEmail alteracao, Guid? atualizadoPor, CancellationToken cancellationToken = default)
        {
            Salvas.Add(alteracao);
            return Task.CompletedTask;
        }

        public Task RemoverAsync(CancellationToken cancellationToken = default)
        {
            Removida = true;
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> ErrosAparencia { get; set; } = [];

        public List<AlteracaoAparenciaEmail> Aparencias { get; } = [];

        public Action<AlteracaoAparenciaEmail>? AoSalvarAparencia { get; set; }

        public IReadOnlyList<string> ValidarAparencia(AlteracaoAparenciaEmail aparencia) => ErrosAparencia;

        public Task SalvarAparenciaAsync(AlteracaoAparenciaEmail aparencia, Guid? atualizadoPor, CancellationToken cancellationToken = default)
        {
            Aparencias.Add(aparencia);
            AoSalvarAparencia?.Invoke(aparencia);
            return Task.CompletedTask;
        }

        public string Previa(AlteracaoAparenciaEmail aparencia, string? urlPublicaPortal) => $"previa:{aparencia.Tema}:{urlPublicaPortal}";

        public bool EnderecoValido(string? email) => email is not null && email.Contains('@', StringComparison.Ordinal);

        public Task<string?> EnviarTesteAsync(string para, CancellationToken cancellationToken = default)
        {
            Testes.Add(para);
            return Task.FromResult(FalhaTeste);
        }
    }

    private sealed class CofreFake : IEscritorSegredos
    {
        public List<(string Referencia, string Valor)> Definidos { get; } = [];

        public ValueTask DefinirAsync(string referencia, string valor, CancellationToken cancellationToken = default)
        {
            Definidos.Add((referencia, valor));
            return ValueTask.CompletedTask;
        }
    }
}
