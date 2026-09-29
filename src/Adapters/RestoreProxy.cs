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
/// A request without the name of a run being watched is refused.
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
        logger.LogInformation("Restores reach the network through the proxy on port {Port}", Port);

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

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                if (await HeadAsync(stream, cancellationToken) is not { } head)
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
                await ForwardAsync(stream, request, head, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
            {
                logger.LogDebug(exception, "A restore's connection through the proxy ended");
            }
        }
    }

    /// <summary>Connects to the host the request names and passes bytes both ways until either side is done.</summary>
    private static async Task ForwardAsync(NetworkStream stream, Request request, byte[] head, CancellationToken cancellationToken)
    {
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(request.Host, request.Port, cancellationToken);
        NetworkStream far = upstream.GetStream();

        if (request.IsTunnel)
            await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), cancellationToken);
        else
            await far.WriteAsync(request.Forwarded(head), cancellationToken);

        Task outbound = stream.CopyToAsync(far, cancellationToken);
        Task inbound = far.CopyToAsync(stream, cancellationToken);
        await Task.WhenAny(outbound, inbound);
    }

    /// <summary>The request's head up to the blank line; null when the client said nothing or said too much.</summary>
    private static async Task<byte[]?> HeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        List<byte> read = [];
        var one = new byte[1];

        while (read.Count < MaxHeadBytes)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
                return null;

            read.Add(one[0]);
            if (read.Count >= 4 && read[^4] == '\r' && read[^3] == '\n' && read[^2] == '\r' && read[^1] == '\n')
                return [.. read];
        }

        return null;
    }

    private static Task RefuseAsync(NetworkStream stream, string status, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nProxy-Authenticate: Basic realm=\"sandbox\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken).AsTask();

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
                return colon > 0 && int.TryParse(first[1][(colon + 1)..], out var port)
                    ? new Request(first[1][..colon].Trim('[', ']'), port, true, run, first[1], lines[0])
                    : null;
            }

            return Uri.TryCreate(first[1], UriKind.Absolute, out var target) && target.Scheme == Uri.UriSchemeHttp
                ? new Request(target.Host, target.Port, false, run, first[1], lines[0])
                : null;
        }

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
