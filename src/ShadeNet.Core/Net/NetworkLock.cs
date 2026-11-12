using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ShadeNet.Core.Engine;

namespace ShadeNet.Core.Net;

/// <summary>
/// Where ShadeNet may reach the network: anywhere, or only through one adapter (<see cref="EngineSettings.NetworkAdapter"/>).
/// </summary>
/// <param name="Locked">Bound to an adapter: only its addresses are used, and nothing at all while it is not connected.</param>
/// <param name="AdapterName">The adapter's name ("Proton VPN"), to tell the user what is waited for.</param>
public sealed record NetworkBinding(bool Locked, string? AdapterName, IPAddress? V4, IPAddress? V6)
{
    public static NetworkBinding Open { get; } = new(false, null, null, null);

    /// <summary>Bound to an adapter that is not connected: nothing may go out.</summary>
    public bool Offline => Locked && V4 == null && V6 == null;

    /// <summary>The adapter's address of this kind (null when not bound, or when it has none of that kind).</summary>
    public IPAddress? LocalFor(AddressFamily family) => family == AddressFamily.InterNetworkV6 ? V6 : family == AddressFamily.InterNetwork ? V4 : null;

    /// <summary>A kind of address that may be used: any when not bound, else one the adapter has.</summary>
    public bool Allows(AddressFamily family) => !Locked || LocalFor(family) != null;

    /// <summary>"Waiting for Proton VPN to connect…"</summary>
    public string WaitingText => $"Waiting for {AdapterName} to connect…";
}

/// <summary>The chosen adapter is not connected: nothing goes out until it is (a download waits rather than failing).</summary>
public sealed class NetworkAdapterOfflineException : IOException
{
    public NetworkAdapterOfflineException(string? adapterName)
        : base($"{adapterName ?? "The chosen network adapter"} is not connected, and ShadeNet uses no other network (Settings > Network).") =>
        AdapterName = adapterName;

    public string? AdapterName { get; }

    /// <summary>Whether a failure comes from the adapter being gone (anywhere in its inner exceptions).</summary>
    public static bool IsIn(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is NetworkAdapterOfflineException) return true;
        return ex is AggregateException agg && agg.InnerExceptions.Any(IsIn);
    }
}

/// <summary>
/// Binds ShadeNet's connections to one network adapter (a VPN's, typically), and holds them while it is not connected: a kill
/// switch. Every connection the engine makes goes through <see cref="ConnectAsync"/> (HTTP downloads, streams, torrents' trackers
/// and peers); yt-dlp is given the adapter's address, ffmpeg a relay bound to it (see <see cref="LocalRelay"/>).
/// </summary>
public static class NetworkLock
{
    static readonly object Gate = new();
    static (string? Id, NetworkBinding Binding, long At) _cache;
    static bool _watching;

    /// <summary>Finds an adapter by its Windows id; tests replace it.</summary>
    internal static Func<string, Torrents.NetworkAdapterInfo?> FindAdapter { get; set; } = Torrents.NetworkAdapters.Find;

    /// <summary>The binding the settings ask for, as the adapters are now (looked up again after a second, or at once after a change).</summary>
    public static NetworkBinding Resolve(EngineSettings settings)
    {
        var id = settings.NetworkAdapter;
        if (string.IsNullOrEmpty(id)) return NetworkBinding.Open;
        lock (Gate)
        {
            if (!_watching)
            {
                NetworkChange.NetworkAddressChanged += (_, _) => Invalidate();
                NetworkChange.NetworkAvailabilityChanged += (_, _) => Invalidate();
                _watching = true;
            }

            if (_cache.Id == id && Environment.TickCount64 - _cache.At < 1000) return Name(_cache.Binding, settings);
        }

        var binding = Lookup(id);
        lock (Gate) _cache = (id, binding, Environment.TickCount64);
        return Name(binding, settings);
    }

    static NetworkBinding Name(NetworkBinding b, EngineSettings settings) =>
        b.AdapterName != null ? b : b with { AdapterName = settings.NetworkAdapterName ?? "the chosen network adapter" };

    static NetworkBinding Lookup(string id)
    {
        var adapter = FindAdapter(id);
        if (adapter is not { Connected: true }) return new NetworkBinding(true, adapter?.Name, null, null);
        var v4 = adapter.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var v6 = adapter.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        return new NetworkBinding(true, adapter.Name, v4, v6);
    }

    /// <summary>The adapters changed (or the setting did): the next look is fresh.</summary>
    public static void Invalidate()
    {
        lock (Gate) _cache = default;
    }

    /// <summary>
    /// A TCP connection to a host, bound to the adapter's address when bound to one (so it leaves through that adapter or not at
    /// all), of the given kind when one is asked for. Throws <see cref="NetworkAdapterOfflineException"/> while the adapter is gone.
    /// </summary>
    public static async Task<Socket> ConnectAsync(string host, int port, AddressFamily family, NetworkBinding binding, CancellationToken ct)
    {
        if (binding.Offline) throw new NetworkAdapterOfflineException(binding.AdapterName);
        var specific = family is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;

        if (!binding.Locked && !specific)
        {
            // as .NET does it itself: one socket for IPv4 and IPv6, every address of the name tried in turn
            var any = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await any.ConnectAsync(new DnsEndPoint(host.Trim('[', ']'), port), ct).ConfigureAwait(false);
                return any;
            }
            catch
            {
                any.Dispose();
                throw;
            }
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) addresses = new[] { literal };
        else addresses = await Dns.GetHostAddressesAsync(host, specific ? family : AddressFamily.Unspecified, ct).ConfigureAwait(false);

        Exception? last = null;
        var tried = false;
        foreach (var address in addresses)
        {
            if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;
            if (specific && address.AddressFamily != family) continue;
            if (!binding.Allows(address.AddressFamily)) continue;
            tried = true;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (binding.LocalFor(address.AddressFamily) is { } local) socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                socket.Dispose();
                ct.ThrowIfCancellationRequested();
                last = ex;
            }
        }

        if (!tried && binding.Locked && addresses.Length > 0)
            throw new SocketException((int)SocketError.AddressFamilyNotSupported);   // (an IPv6-only site through an adapter with only IPv4)
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Waits until the adapter is connected again (at once when it is, or when nothing is bound).</summary>
    public static async Task WaitUntilOnlineAsync(EngineSettings settings, Action<NetworkBinding>? waiting, CancellationToken ct)
    {
        var told = false;
        while (Resolve(settings) is { Offline: true } binding)
        {
            if (!told)
            {
                waiting?.Invoke(binding);
                told = true;
            }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The address yt-dlp should send from (its <c>--source-address</c>): the adapter's IPv4 address, else its IPv6 one.</summary>
    public static string? SourceAddress(NetworkBinding binding) => binding.Locked ? (binding.V4 ?? binding.V6)?.ToString() : null;
}
