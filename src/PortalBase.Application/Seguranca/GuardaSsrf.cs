using System.Net;
using System.Net.Sockets;

namespace PortalBase.Application.Seguranca;

/// <summary>
/// Proteção contra SSRF (Prompt 07, seção 18). Classifica endereços IP internos/sensíveis
/// (loopback, RFC1918, link-local/metadata 169.254.169.254, CGNAT, ULA IPv6) e valida destinos
/// de saída controláveis. Todo cliente HTTP futuro que use URL de origem controlável DEVE
/// validar o destino aqui. Endpoints internos legítimos passam por allowlist explícita.
/// </summary>
public static class GuardaSsrf
{
    /// <summary>Somente http/https são aceitos como destino (bloqueia file:, gopher:, etc.).</summary>
    public static bool EsquemaPermitido(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal));
    }

    /// <summary>O IP é interno/sensível e não deve ser alvo de requisição de saída?</summary>
    public static bool IpBloqueado(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                  // 0.0.0.0/8
                10 => true,                                 // RFC1918 10/8
                127 => true,                                // loopback
                169 when b[1] == 254 => true,               // link-local + metadata 169.254.169.254
                172 when b[1] is >= 16 and <= 31 => true,   // RFC1918 172.16/12
                192 when b[1] == 168 => true,               // RFC1918 192.168/16
                100 when b[1] is >= 64 and <= 127 => true,  // CGNAT RFC6598 100.64/10
                _ => false,
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || IPAddress.IPv6Loopback.Equals(ip))
            {
                return true;
            }

            // Unique Local Address fc00::/7
            return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        return true; // família desconhecida: bloqueia por precaução (fail-closed)
    }

    /// <summary>
    /// Valida um destino de saída: esquema http/https, host não em endereço interno (a menos que
    /// esteja na allowlist explícita de endpoints internos legítimos). Resolve o host via DNS.
    /// </summary>
    public static async Task<bool> DestinoPermitidoAsync(
        Uri uri,
        IReadOnlySet<string> hostsPermitidos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(hostsPermitidos);

        if (!EsquemaPermitido(uri))
        {
            return false;
        }

        if (hostsPermitidos.Contains(uri.Host))
        {
            return true;
        }

        var enderecos = IPAddress.TryParse(uri.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);

        return enderecos.Length > 0 && !Array.Exists(enderecos, IpBloqueado);
    }
}
