using System.Net;
using System.Net.Sockets;

namespace RemotePlay;

internal static class PublicAddress
{
    private static readonly Uri DiscoveryEndpoint = new("https://api.ipify.org");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 64
    };

    internal static async Task<IPAddress> FindAsync(CancellationToken stop)
    {
        string response = await Client.GetStringAsync(DiscoveryEndpoint, stop).ConfigureAwait(false);
        if (!IPAddress.TryParse(response.Trim(), out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
            throw new IOException("El servicio no devolvió una dirección IPv4 válida.");
        return address;
    }
}
