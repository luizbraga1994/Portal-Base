using FluentAssertions;
using PortalBase.Application.Abstracoes;
using PortalBase.Application.Marca;
using PortalBase.Application.Multitenancy;
using PortalBase.Application.Tests.Dobras;
using PortalBase.Domain.Marca;

namespace PortalBase.Application.Tests.Marca;

/// <summary>
/// UI-005 (auditoria de 08/10/2026), teste permanente: o layout lia a imagem inteira da logo (até 10 MB) do banco a cada
/// navegação só para saber se havia logo. Agora usa o RESUMO (metadados, sem bytes) com cache curto por processo —
/// invalidado na alteração, separado por contribuinte (chave do servidor) e fail-closed sem contribuinte.
/// </summary>
public sealed class ResumoMarcaTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] OutroPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 9];
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly Relogio _relogio = new() { AgoraUtc = T0 };
    private readonly CacheResumoMarca _cache = new();
    private readonly RepositorioPlataforma _plataforma = new();
    private readonly RepositorioTenant _repositorioTenant = new();
    private readonly ContextoTenantFake _tenant = new() { TenantId = Guid.NewGuid() };
    private readonly ContextoSegurancaFake _seguranca = new() { PrincipalId = Guid.NewGuid().ToString(), EstaAutenticado = true };

    private ServicoMarcaPlataforma ServicoPlataforma()
        => new(_plataforma, new AutorizacaoPlataformaFake(), _seguranca, new AuditoriaFake(), _relogio, _cache);

    private ServicoMarcaTenant ServicoTenant()
        => new(_repositorioTenant, _tenant, _seguranca, new ServicoAutorizacaoFake { Permitir = true }, new AuditoriaFake(), _relogio, _cache);

    [Fact]
    public async Task Resumo_Da_Logo_Global_Nao_Le_Os_Bytes_E_Vem_Do_Cache_Na_Proxima_Navegacao()
    {
        _plataforma.Marca = MarcaVisual.Criar(Png, "image/png", T0, null);

        var primeiro = await ServicoPlataforma().ObterResumoAsync();
        var segundo = await ServicoPlataforma().ObterResumoAsync();

        primeiro.Should().Be(new ResumoMarcaVisual("image/png", _plataforma.Marca.HashSha256, Png.Length));
        segundo.Should().Be(primeiro);
        _plataforma.LeiturasResumo.Should().Be(1, "a segunda navegação usa o cache");
        _plataforma.LeiturasComBytes.Should().Be(0, "o resumo nunca lê a imagem");
    }

    [Fact]
    public async Task Ausencia_De_Logo_Tambem_Fica_Em_Cache_E_Expira()
    {
        (await ServicoPlataforma().ObterResumoAsync()).Should().BeNull();
        (await ServicoPlataforma().ObterResumoAsync()).Should().BeNull();
        _plataforma.LeiturasResumo.Should().Be(1);

        _relogio.AgoraUtc = T0 + CacheResumoMarca.Validade;
        await ServicoPlataforma().ObterResumoAsync();
        _plataforma.LeiturasResumo.Should().Be(2, "passada a validade, consulta de novo (outra instância pode ter mudado a logo)");
    }

    [Fact]
    public async Task Trocar_A_Logo_Global_Invalida_O_Cache_Na_Hora()
    {
        _plataforma.Marca = MarcaVisual.Criar(Png, "image/png", T0, null);
        var antes = await ServicoPlataforma().ObterResumoAsync();

        await ServicoPlataforma().DefinirAsync(OutroPng, "image/png");
        var depois = await ServicoPlataforma().ObterResumoAsync();

        depois!.HashSha256.Should().NotBe(antes!.HashSha256);
        depois.TamanhoBytes.Should().Be(OutroPng.Length);
    }

    [Fact]
    public async Task Resumo_Do_Contribuinte_E_Separado_Por_Contribuinte_E_Invalidado_Na_Troca()
    {
        _repositorioTenant.Marca = MarcaVisual.Criar(Png, "image/png", T0, null);
        var tenantA = _tenant.TenantId!.Value;

        var deA = await ServicoTenant().ObterResumoAsync();
        _tenant.TenantId = Guid.NewGuid();
        _repositorioTenant.Marca = null;
        var deB = await ServicoTenant().ObterResumoAsync();

        deA.Should().NotBeNull();
        deB.Should().BeNull("o cache de um contribuinte nunca responde por outro");
        _repositorioTenant.LeiturasResumo.Should().Be(2);

        _tenant.TenantId = tenantA;
        _repositorioTenant.Marca = MarcaVisual.Criar(Png, "image/png", T0, null);
        await ServicoTenant().DefinirAsync(OutroPng, "image/png");
        (await ServicoTenant().ObterResumoAsync())!.TamanhoBytes.Should().Be(OutroPng.Length);
        _repositorioTenant.LeiturasComBytes.Should().Be(1, "só a alteração lê a logo inteira");
    }

    [Fact]
    public async Task Sem_Contribuinte_Autorizado_Nao_Ha_Resumo()
    {
        _repositorioTenant.Marca = MarcaVisual.Criar(Png, "image/png", T0, null);
        _tenant.Estado = EstadoTenant.Invalido;

        (await ServicoTenant().ObterResumoAsync()).Should().BeNull();
        _repositorioTenant.LeiturasResumo.Should().Be(0, "fail-closed: nem consulta");
    }

    private sealed class Relogio : IRelogio
    {
        public DateTimeOffset AgoraUtc { get; set; }
    }

    private sealed class RepositorioPlataforma : IRepositorioMarcaPlataforma
    {
        public MarcaVisual? Marca { get; set; }

        public int LeiturasResumo { get; private set; }

        public int LeiturasComBytes { get; private set; }

        public Task<MarcaVisual?> ObterAsync(CancellationToken cancellationToken = default)
        {
            LeiturasComBytes++;
            return Task.FromResult(Marca);
        }

        public Task<MarcaVisual?> ObterParaExibicaoAsync(CancellationToken cancellationToken = default)
        {
            LeiturasComBytes++;
            return Task.FromResult(Marca);
        }

        public Task<ResumoMarcaVisual?> ObterResumoAsync(CancellationToken cancellationToken = default)
        {
            LeiturasResumo++;
            return Task.FromResult(Marca is null ? null : new ResumoMarcaVisual(Marca.ContentType, Marca.HashSha256, Marca.TamanhoBytes));
        }

        public Task SalvarAsync(MarcaVisual marca, CancellationToken cancellationToken = default)
        {
            Marca = marca;
            return Task.CompletedTask;
        }
    }

    private sealed class RepositorioTenant : IRepositorioMarcaTenant
    {
        public MarcaVisual? Marca { get; set; }

        public int LeiturasResumo { get; private set; }

        public int LeiturasComBytes { get; private set; }

        public Task<MarcaVisual?> ObterAsync(CancellationToken cancellationToken = default)
        {
            LeiturasComBytes++;
            return Task.FromResult(Marca);
        }

        public Task<ResumoMarcaVisual?> ObterResumoAsync(CancellationToken cancellationToken = default)
        {
            LeiturasResumo++;
            return Task.FromResult(Marca is null ? null : new ResumoMarcaVisual(Marca.ContentType, Marca.HashSha256, Marca.TamanhoBytes));
        }

        public Task SalvarAsync(MarcaVisual marca, CancellationToken cancellationToken = default)
        {
            Marca = marca;
            return Task.CompletedTask;
        }
    }
}
