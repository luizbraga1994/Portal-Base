using FluentAssertions;
using PortalBase.Infrastructure.Segredos;
using PortalBase.Integration.Tests.Infra;

namespace PortalBase.Integration.Tests.Seguranca;

/// <summary>Cache de segredos: TTL curto e invalidação para rotação (Prompt 06, seções 11, 12).</summary>
public sealed class CacheSegredosTests
{
    [Fact]
    public async Task Cache_Evita_Nova_Busca_Dentro_Do_Ttl()
    {
        var interno = new ProvedorSegredosFake { Valores = { ["k"] = "v1" } };
        var relogio = new RelogioFixo(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
        var cache = new ProvedorSegredosCache(interno, relogio, TimeSpan.FromMinutes(5));

        (await cache.ObterAsync("k")).Should().Be("v1");
        (await cache.ObterAsync("k")).Should().Be("v1");
        interno.Chamadas.Should().Be(1); // segunda leitura veio do cache
    }

    [Fact] // §12 — invalidar permite obter a nova versão (rotação)
    public async Task Invalidar_Busca_Nova_Versao()
    {
        var interno = new ProvedorSegredosFake { Valores = { ["k"] = "v1" } };
        var relogio = new RelogioFixo(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
        var cache = new ProvedorSegredosCache(interno, relogio, TimeSpan.FromMinutes(5));

        (await cache.ObterAsync("k")).Should().Be("v1");
        interno.Valores["k"] = "v2"; // rotação no cofre
        cache.Invalidar("k");

        (await cache.ObterAsync("k")).Should().Be("v2");
        interno.Chamadas.Should().Be(2);
    }
}
