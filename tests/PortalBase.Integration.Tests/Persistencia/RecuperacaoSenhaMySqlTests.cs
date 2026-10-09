using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Auditoria;
using PortalBase.Application.Autorizacao;
using PortalBase.Application.Identidade;
using PortalBase.Application.Notificacoes;
using PortalBase.Infrastructure.Persistencia.Catalogo;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Persistencia;

/// <summary>
/// "Esqueci a senha" contra o MySQL real: o link é guardado só como hash (HMAC real), o consumo é ATÔMICO no banco
/// (dois envios simultâneos do mesmo link: exatamente um redefine), os outros links são invalidados e a senha nova
/// autentica pelo caso de uso real de login. Pulado sem <c>PORTALBASE_TEST_MYSQL_ADMIN</c>.
/// </summary>
[Collection(ColecaoMultiTenant.Nome)]
public sealed class RecuperacaoSenhaMySqlTests
{
    private const string SenhaNova = "Senha-Nova#2026x";
    private readonly FixtureMultiTenant _fixture;

    public RecuperacaoSenhaMySqlTests(FixtureMultiTenant fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Link_Vale_Uma_Vez_Mesmo_Com_Dois_Envios_Simultaneos()
    {
        Skip.IfNot(_fixture.Disponivel, "MySQL de teste indisponível.");
        var (usuarioId, login) = await CenarioIdentidade.CriarUsuarioComSenhaAsync(_fixture, "Senha-Antiga#2026");
        var email = login + "@exemplo.com";
        var fila = new FilaCaptura();

        using (var escopo = _fixture.CriarEscopo())
        {
            (await Recuperacao(escopo, fila).SolicitarAsync(email.ToUpperInvariant())).Should().Be(ResultadoSolicitacaoRecuperacao.Aceita);
            (await Recuperacao(escopo, fila).SolicitarAsync(email)).Should().Be(ResultadoSolicitacaoRecuperacao.Aceita);
        }

        fila.Mensagens.Should().HaveCount(2);
        var token = ExtrairToken(fila.Mensagens[0]);
        var outroToken = ExtrairToken(fila.Mensagens[1]);
        using (var escopo = _fixture.CriarEscopo())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<PortalBaseCatalogoDbContext>();
            var hashes = await catalogo.TokensRecuperacao.AsNoTracking().Where(t => t.UsuarioId == usuarioId).Select(t => t.HashToken).ToListAsync();
            hashes.Should().HaveCount(2).And.NotContain(token, "só o hash vai para o banco");
            (await escopo.ServiceProvider.GetRequiredService<IRepositorioUsuario>()
                .ContarTokensRecuperacaoDesdeAsync(usuarioId, DateTimeOffset.UtcNow.AddHours(-1))).Should().Be(2);
        }

        // Dois envios SIMULTÂNEOS do mesmo link, cada um no seu escopo (como duas requisições).
        async Task<ResultadoRedefinicaoSenha> Redefinir()
        {
            using var escopo = _fixture.CriarEscopo();
            return await Recuperacao(escopo, fila).RedefinirAsync(token, SenhaNova, SenhaNova);
        }

        var resultados = await Task.WhenAll(Redefinir(), Redefinir());
        resultados.Count(r => r == ResultadoRedefinicaoSenha.Redefinida).Should().Be(1);
        resultados.Count(r => r == ResultadoRedefinicaoSenha.LinkInvalido).Should().Be(1);

        using (var escopo = _fixture.CriarEscopo())
        {
            (await Recuperacao(escopo, fila).LinkValidoAsync(outroToken)).Should().BeFalse("os outros links abertos foram invalidados");
            var autenticar = escopo.ServiceProvider.GetRequiredService<AutenticarUsuario>();
            (await autenticar.ExecutarAsync(email, SenhaNova)).UsuarioId.Should().Be(usuarioId);
            var antiga = async () => await autenticar.ExecutarAsync(email, "Senha-Antiga#2026");
            await antiga.Should().ThrowAsync<Application.Excecoes.CredenciaisInvalidasException>();
        }
    }

    private static RecuperacaoSenha Recuperacao(IServiceScope escopo, IFilaEmail fila)
    {
        var sp = escopo.ServiceProvider;
        return new RecuperacaoSenha(
            sp.GetRequiredService<IRepositorioUsuario>(),
            sp.GetRequiredService<IGeradorTokens>(),
            sp.GetRequiredService<IHashDeterministico>(),
            sp.GetRequiredService<IHasherSenha>(),
            fila,
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IAuditoria>(),
            sp.GetRequiredService<IRelogio>(),
            OpcoesRecuperacaoSenha.Padrao("https://portal.exemplo.com.br"));
    }

    private static string ExtrairToken(MensagemEmail mensagem)
    {
        var marcador = "token=";
        var inicio = mensagem.Texto.IndexOf(marcador, StringComparison.Ordinal) + marcador.Length;
        var fim = mensagem.Texto.IndexOfAny([' ', '\n', '\r'], inicio);
        return Uri.UnescapeDataString(mensagem.Texto[inicio..fim]);
    }

    private sealed class FilaCaptura : IFilaEmail
    {
        public bool Habilitado => true;

        public List<MensagemEmail> Mensagens { get; } = [];

        public bool Enfileirar(MensagemEmail mensagem)
        {
            lock (Mensagens)
            {
                Mensagens.Add(mensagem);
            }

            return true;
        }
    }
}
