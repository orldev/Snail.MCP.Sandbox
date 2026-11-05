using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>The door a restore reaches the network through, which writes down every host each test run asked for.</summary>
/// <remarks>
/// The platform shows the human which hosts a run reached, and only the sandbox can know.
/// Naming the package index the toolchain would use is a guess: a project names its own index in a nuget.config, an
/// .npmrc or its requirements, a restore runs setup.py and install scripts that go anywhere, and a warm package volume
/// goes nowhere. So the restore is handed this proxy in its environment, with the run's own name as credentials, and
/// the proxy records the host of every request under that run. Where the restore network reaches nothing but the
/// proxy, the list is exact; on an open network, a program that ignores the proxy goes around it, so it is a lower bound.
/// A request without the name of a run being watched is refused, and so is one for an address of the server's own
/// network — loopback, link-local, private — unless the deployment names the host as a mirror: the proxy connects out
/// from where the node's gateway and the cluster's APIs are reachable, and a restore script must not.
/// </remarks>
public sealed class RestoreProxy(IOptions<SandboxOptions> options, ILogger<RestoreProxy> logger) : BackgroundService
{
    /// <summary>The longest request head read before the request is refused.</summary>
    internal const int MaxHeadBytes = 16 * 1024;

    private readonly ConcurrentDictionary<string, Watched> _runs = new(StringComparer.Ordinal);
    private TcpListener? _listener;

    /// <summary>The port the proxy listens on, once it does; zero before it starts or when it is off.</summary>
    public int Port { get; private set; }

    /// <summary>Starts watching one run: its proxy address for the restore's environment, and the hosts it asks for; null when the proxy is off.</summary>
    public RestoreWatch? Watch()
    {
        if (options.Value.RestoreProxy is not { Length: > 0 } address)
            return null;

        string run = Guid.NewGuid().ToString("N");
        var watched = new Watched();
        _runs[run] = watched;

        var proxy = new UriBuilder(address) { UserName = run, Password = "x" }.Uri.ToString().TrimEnd('/');

        return new RestoreWatch(proxy, watched, () => _runs.TryRemove(run, out _));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.RestoreProxyPort <= 0)
            return;

        _listener = new TcpListener(IPAddress.Any, options.Value.RestoreProxyPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log.ProxyListening(logger, Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = ServeAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }

    /// <summary>Releases the listener with the service; it was stopped already when the service stopped.</summary>
    public override void Dispose()
    {
        _listener?.Dispose();
        base.Dispose();
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                if (await HeadAsync(stream, cancellationToken) is not var (head, beyond))
                    return;

                if (Request.Of(head) is not { } request)
                {
                    await RefuseAsync(stream, "400 Bad Request", cancellationToken);
                    return;
                }

                if (request.Run is not { } run || !_runs.TryGetValue(run, out var watched))
                {
                    await RefuseAsync(stream, "407 Proxy Authentication Required", cancellationToken);
                    return;
                }

                watched.Saw(request.Host);
                if (await ReachableAsync(request.Host, options.Value.RestoreHosts, cancellationToken) is not { } addresses)
                {
                    await RefuseAsync(stream, "403 Forbidden", cancellationToken);
                    return;
                }

                await ForwardAsync(client, request, head, beyond, addresses, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
            {
                Log.ProxyConnectionEnded(logger, exception);
            }
        }
    }

    /// <summary>The addresses the host resolves to, or null when one of them is inside and the host is not named.</summary>
    /// <remarks>
    /// Resolved once, and the connection goes to what was resolved rather than to the name again: a name that answers a
    /// public address to this question and a private one a moment later gains nothing by it.
    /// </remarks>
    private static async Task<IPAddress[]?> ReachableAsync(string host, IReadOnlyCollection<string> named, CancellationToken cancellationToken)
    {
        IPAddress[] resolved = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);

        if (named.Contains(host, StringComparer.OrdinalIgnoreCase))
            return resolved;

        return resolved.Length > 0 && resolved.All(IsOutside) ? resolved : null;
    }

    /// <summary>Whether an address is on the internet rather than on the node, its link or its private network.</summary>
    private static bool IsOutside(IPAddress address)
    {
        var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(plain) || plain.Equals(IPAddress.Any) || plain.Equals(IPAddress.IPv6Any) || plain.IsIPv6LinkLocal || plain.IsIPv6UniqueLocal || plain.IsIPv6SiteLocal)
            return false;

        if (plain.AddressFamily != AddressFamily.InterNetwork)
            return true;

        var bytes = plain.GetAddressBytes();
        var isPrivate = bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);

        return !isPrivate;
    }

    /// <summary>Connects to the addresses the host resolved to and passes bytes both ways until both sides are done.</summary>
    /// <remarks>
    /// Each direction ends on its own: a side that has said everything half-closes the other, and the answer still
    /// comes back. Ending the tunnel at the first direction to finish cut off a reply a client had already asked for
    /// and said nothing more about, and left the other copy failing on a stream closed under it.
    /// </remarks>
    private static async Task ForwardAsync(TcpClient client, Request request, byte[] head, ReadOnlyMemory<byte> beyond, IPAddress[] addresses, CancellationToken cancellationToken)
    {
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(addresses, request.Port, cancellationToken);
        NetworkStream near = client.GetStream();
        NetworkStream far = upstream.GetStream();

        if (request.IsTunnel)
            await near.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), cancellationToken);
        else
            await far.WriteAsync(request.Forwarded(head), cancellationToken);

        if (beyond.Length > 0)
            await far.WriteAsync(beyond, cancellationToken);

        using var ending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task outbound = PassAsync(near, far, upstream.Client, ending);
        Task inbound = PassAsync(far, near, client.Client, ending);
        await Task.WhenAll(outbound, inbound);
    }

    /// <summary>Copies one direction to its end, then tells the far side there is no more; a direction that breaks ends the other.</summary>
    private static async Task PassAsync(NetworkStream source, NetworkStream target, Socket targetSocket, CancellationTokenSource ending)
    {
        try
        {
            await source.CopyToAsync(target, ending.Token);
            targetSocket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            await ending.CancelAsync();
        }
    }

    /// <summary>The request's head up to the blank line, and whatever the client sent after it; null when the client said nothing or said too much.</summary>
    /// <remarks>
    /// Read a packet at a time rather than a byte: a byte per read cost a restore of hundreds of requests a system call
    /// per byte of every head. What arrives past the blank line in the same packet — the start of a body, the first
    /// bytes of a tunnel's handshake — belongs to the host and is handed on with the head.
    /// </remarks>
    private static async Task<(byte[] Head, ReadOnlyMemory<byte> Beyond)?> HeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var read = new byte[MaxHeadBytes];
        var count = 0;

        while (count < read.Length)
        {
            var got = await stream.ReadAsync(read.AsMemory(count), cancellationToken);
            if (got == 0)
                return null;

            var searchFrom = Math.Max(0, count - 3);
            count += got;

            var blank = read.AsSpan(searchFrom, count - searchFrom).IndexOf(BlankLine);
            if (blank >= 0)
                return (read[..(searchFrom + blank + BlankLine.Length)], read.AsMemory(searchFrom + blank + BlankLine.Length, count - searchFrom - blank - BlankLine.Length));
        }

        return null;
    }

    private static ReadOnlySpan<byte> BlankLine => "\r\n\r\n"u8;

    private static Task RefuseAsync(NetworkStream stream, string status, CancellationToken cancellationToken)
    {
        var challenge = status.StartsWith("407", StringComparison.Ordinal) ? "Proxy-Authenticate: Basic realm=\"sandbox\"\r\n" : string.Empty;

        return stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{challenge}Content-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken).AsTask();
    }

    /// <summary>The hosts one run asked for, each once, in the order it first asked.</summary>
    internal sealed class Watched
    {
        private readonly Lock _gate = new();
        private readonly List<string> _hosts = [];

        public IReadOnlyList<string> Hosts
        {
            get
            {
                lock (_gate)
                    return [.. _hosts];
            }
        }

        public void Saw(string host)
        {
            lock (_gate)
            {
                if (!_hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                    _hosts.Add(host.ToLowerInvariant());
            }
        }
    }

    /// <summary>One request as the proxy reads it: where it goes, and which run asked.</summary>
    internal sealed record Request(string Host, int Port, bool IsTunnel, string? Run, string Target, string Line)
    {
        /// <summary>The request read from its head; null when it is neither a tunnel nor a request for an absolute address.</summary>
        public static Request? Of(byte[] head)
        {
            string[] lines = Encoding.ASCII.GetString(head).Split("\r\n");
            string[] first = lines[0].Split(' ');
            if (first.Length != 3)
                return null;

            string? run = RunOf(lines.Skip(1));

            if (string.Equals(first[0], "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                int colon = first[1].LastIndexOf(':');
                return colon > 0 && int.TryParse(first[1][(colon + 1)..], out var port) && IsAPort(port)
                    ? new Request(first[1][..colon].Trim('[', ']'), port, true, run, first[1], lines[0])
                    : null;
            }

            return Uri.TryCreate(first[1], UriKind.Absolute, out var target) && target.Scheme == Uri.UriSchemeHttp && IsAPort(target.Port)
                ? new Request(target.Host, target.Port, false, run, first[1], lines[0])
                : null;
        }

        /// <summary>Whether a number is a port a socket can be asked to connect to; past the range the connection throws instead of refusing.</summary>
        private static bool IsAPort(int port) => port is >= 1 and <= 65535;

        /// <summary>The head as the host expects it: the path instead of the whole address, and nothing meant for the proxy.</summary>
        public byte[] Forwarded(byte[] head)
        {
            string[] lines = Encoding.ASCII.GetString(head).Split("\r\n");
            string[] first = lines[0].Split(' ');
            var target = new Uri(Target);

            IEnumerable<string> kept = lines.Skip(1).Where(line => !line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase));

            return Encoding.ASCII.GetBytes(string.Join("\r\n", [$"{first[0]} {target.PathAndQuery} {first[2]}", .. kept]));
        }

        private static string? RunOf(IEnumerable<string> headers)
        {
            string? authorization = headers.FirstOrDefault(line => line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
            string? encoded = authorization?["Proxy-Authorization:".Length..].Trim() is { } value && value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                ? value[6..].Trim()
                : null;

            if (encoded is null)
                return null;

            try
            {
                string pair = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                int colon = pair.IndexOf(':');

                return colon > 0 ? pair[..colon] : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}

/// <summary>One run watched by the restore proxy: the address its restore is given, and the hosts it asked for so far.</summary>
public sealed class RestoreWatch : IDisposable
{
    private readonly RestoreProxy.Watched _watched;
    private readonly Action _ended;

    internal RestoreWatch(string proxy, RestoreProxy.Watched watched, Action ended)
    {
        Proxy = proxy;
        _watched = watched;
        _ended = ended;
    }

    /// <summary>The proxy's address with the run's name in it, as the restore's environment carries it.</summary>
    public string Proxy { get; }

    /// <summary>Every host the run asked for, each once.</summary>
    public IReadOnlyList<string> Hosts => _watched.Hosts;

    /// <summary>Stops watching: a request with this run's name is refused from now on.</summary>
    public void Dispose() => _ended();
}
