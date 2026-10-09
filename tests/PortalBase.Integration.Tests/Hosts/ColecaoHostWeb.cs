namespace PortalBase.Integration.Tests.Hosts;

/// <summary>
/// Coleção que SERIALIZA os testes que constroem um host (Web pela WebApplicationFactory, e o Worker). Dois hosts
/// bootando em paralelo disputam estáticos compartilhados (ex.: logger de bootstrap do Serilog),
/// gerando falha intermitente. Reuni-los numa coleção elimina a concorrência entre eles.
/// </summary>
[CollectionDefinition(Nome)]
public sealed class ColecaoHostWeb
{
    public const string Nome = "host-web";
}
