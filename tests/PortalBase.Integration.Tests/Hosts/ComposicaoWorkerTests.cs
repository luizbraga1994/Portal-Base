using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PortalBase.Worker;

namespace PortalBase.Integration.Tests.Hosts;

/// <summary>
/// O host do Worker é montado pela MESMA composição do <c>Program</c> (<see cref="ComposicaoWorker"/>) e construído em
/// <b>Development</b>, onde o .NET liga <c>ValidateOnBuild</c> e <c>ValidateScopes</c>: todo serviço registrado precisa
/// ter as dependências registradas — um serviço registrado sem a sua dependência impede o Worker de iniciar. O teste de
/// boot da Web roda em "Testing", sem essa validação. Fica na coleção dos hosts da Web: o logger estático do Serilog é
/// do processo, e um host da Web construído EM PARALELO o congelava entre a limpeza abaixo e o Build (falha intermitente).
/// </summary>
[Collection(ColecaoHostWeb.Nome)]
public sealed class ComposicaoWorkerTests
{
    [Fact]
    public void Host_Do_Worker_Constroi_Em_Development_Com_Validacao_De_DI()
    {
        // Sem ApplicationName do Worker: não carrega os user-secrets REAIS de quem roda o teste. Sem recarga dos
        // appsettings (file watcher = 1 instância de inotify): o teste valida a DI, não a recarga, e com as suítes em
        // paralelo o limite do Linux (128 por usuário) se esgotava e o host falhava com IOException antes da validação.
        var configuracao = new ConfigurationManager();
        configuracao.AddInMemoryCollection(new Dictionary<string, string?> { ["hostBuilder:reloadConfigOnChange"] = "false" });

        // O AddSerilog do Worker recarrega e CONGELA o logger estático quando ele é o "bootstrap" (ReloadableLogger). No
        // mesmo processo, um host da Web montado antes por outro teste já o congelou ("The logger is already frozen") —
        // falha que dependia da ordem dos testes. Em produção cada host tem o seu processo; aqui começa com um logger limpo
        // (e a coleção garante que nenhum host da Web troca o logger enquanto este host é construído).
        Serilog.Log.Logger = Serilog.Core.Logger.None;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            Args = [],
            Configuration = configuracao,
        });
        ComposicaoWorker.Configurar(builder);

        var construir = () =>
        {
            using var host = builder.Build();
        };

        construir.Should().NotThrow(
            "em Development o .NET valida a DI inteira; um serviço sem dependência registrada impede o Worker de iniciar");
    }
}
