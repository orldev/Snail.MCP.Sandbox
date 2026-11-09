using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>
/// The restore proxy under a restore that is hostile or merely busy: it is the one way out of the restore network, so
/// whatever it forwards to is what a project's restore script reaches.
/// </summary>
public sealed class RestoreProxyChaosTests : IAsyncLifetime
{
    private readonly TcpListener _upstream = new(IPAddress.Loopback, 0);
    private readonly TcpListener _late = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private RestoreProxy _proxy = default!;

    private int UpstreamPort => ((IPEndPoint)_upstream.LocalEndpoint).Port;

    private int LatePort => ((IPEndPoint)_late.LocalEndpoint).Port;

    public async Task InitializeAsync()
    {
        _upstream.Start();
        _late.Start();
        _ = EchoAsync();
        _ = LateEchoAsync();

        int port = Ports.Free();
        _proxy = new RestoreProxy(Options.Create(new SandboxOptions { RestoreProxy = $"http://127.0.0.1:{port}", RestoreProxyPort = port }), NullLogger<RestoreProxy>.Instance);
        await _proxy.StartAsync(_stopping.Token);

        for (var waited = 0; _proxy.Port == 0 && waited < 50; waited++)
            await Task.Delay(20);
    }

    public async Task DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _proxy.StopAsync(CancellationToken.None);
        _upstream.Stop();
        _late.Stop();
    }

    /// <summary>
    /// Breaks the tunnel's end: a client that has sent its whole request and half-closed still gets the answer. The tunnel
    /// used to end at the first direction to finish, which was the client's, and the reply the host had not yet written was
    /// thrown away with the connection.
    /// </summary>
    [Fact]
    public async Task Connect_ClientHalfClosesAfterAsking_StillGetsTheAnswer()
    {
        _proxy = await NamingAsync("127.0.0.1");
        using RestoreWatch watch = _proxy.Watch()!;

        using var client = await ConnectedAsync();
        await AskAsync(client, $"CONNECT 127.0.0.1:{LatePort} HTTP/1.1\r\nProxy-Authorization: Basic {Credentials(watch)}\r\n\r\n");
        NetworkStream talk = client.GetStream();
        await talk.WriteAsync("ping"u8.ToArray());
        client.Client.Shutdown(SocketShutdown.Send);
        string echoed = await ReadAsync(talk, 4);

        Assert.Equal("ping", echoed);
    }

    /// <summary>
    /// Breaks the wall: the proxy used to connect wherever it was asked, so a restore script reached the server's own
    /// loopback and, through the server's network, the node's gateway, where a Nomad or Consul API may listen. A package
    /// mirror on such an address has to be named in RestoreHosts; unnamed, a tunnel there is refused and still written down.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.255.255.1")]
    [InlineData("172.17.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("[::1]")]
    public async Task Connect_AddressOfTheNodesOwnNetwork_IsRefusedAndNamed(string address)
    {
        using RestoreWatch watch = _proxy.Watch()!;

        using var client = await ConnectedAsync();
        string answer = await AskAsync(client, $"CONNECT {address}:{UpstreamPort} HTTP/1.1\r\nProxy-Authorization: Basic {Credentials(watch)}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", answer, StringComparison.Ordinal);
        Assert.Equal([address.Trim('[', ']')], watch.Hosts);
    }

    /// <summary>
    /// Breaks the port: a CONNECT to a port past 65535 parses, and connecting to it throws an argument exception that the
    /// serving task does not catch — the client is closed without a status, and the exception goes unobserved.
    /// </summary>
    [Fact]
    public async Task Connect_PortPastTheRange_IsRefusedWithAStatus()
    {
        using RestoreWatch watch = _proxy.Watch()!;

        using var client = await ConnectedAsync();
        string answer = await AskAsync(client, $"CONNECT 93.184.216.34:99999 HTTP/1.1\r\nProxy-Authorization: Basic {Credentials(watch)}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 4", answer, StringComparison.Ordinal);
    }

    /// <summary>Breaks the accept loop: sixty-four tunnels opened at once all answer, and the host is still named once.</summary>
    [Fact]
    public async Task Connect_SixtyFourTunnelsAtOnce_AllAnswerAndTheHostIsNamedOnce()
    {
        _proxy = await NamingAsync("127.0.0.1");
        using RestoreWatch watch = _proxy.Watch()!;

        string[] answers = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => TunnelledAsync(watch)));

        Assert.All(answers, answer => Assert.StartsWith("HTTP/1.1 200", answer, StringComparison.Ordinal));
        Assert.Equal(["127.0.0.1"], watch.Hosts);
    }

    /// <summary>The same proxy, with the host named as a mirror, in place of the one the fixture started.</summary>
    private async Task<RestoreProxy> NamingAsync(string host)
    {
        await _proxy.StopAsync(CancellationToken.None);
        int port = Ports.Free();
        var named = new RestoreProxy(Options.Create(new SandboxOptions { RestoreProxy = $"http://127.0.0.1:{port}", RestoreProxyPort = port, RestoreHosts = [host] }), NullLogger<RestoreProxy>.Instance);
        await named.StartAsync(_stopping.Token);

        for (var waited = 0; named.Port == 0 && waited < 50; waited++)
            await Task.Delay(20);

        return named;
    }

    private async Task<string> TunnelledAsync(RestoreWatch watch)
    {
        using var client = await ConnectedAsync();

        return await AskAsync(client, $"CONNECT 127.0.0.1:{UpstreamPort} HTTP/1.1\r\nProxy-Authorization: Basic {Credentials(watch)}\r\n\r\n");
    }

    private static string Credentials(RestoreWatch watch) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(new Uri(watch.Proxy).UserInfo)));

    private async Task<TcpClient> ConnectedAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxy.Port);

        return client;
    }

    private static async Task<string> AskAsync(TcpClient client, string request)
    {
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));

        List<byte> head = [];
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await client.GetStream().ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 0)
                break;

            head.Add(one[0]);
        }

        return Encoding.ASCII.GetString([.. head]);
    }

    /// <summary>Reads from a stream taken before the socket was half-closed: a half-closed TcpClient hands out no stream.</summary>
    private static async Task<string> ReadAsync(NetworkStream talk, int bytes)
    {
        var buffer = new byte[bytes];
        var read = 0;
        while (read < bytes)
        {
            int got = await talk.ReadAsync(buffer.AsMemory(read)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (got == 0)
                break;

            read += got;
        }

        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    /// <summary>A host that answers only once the client has said everything: reads to the end, then sends it all back.</summary>
    private async Task LateEchoAsync()
    {
        try
        {
            while (true)
            {
                TcpClient accepted = await _late.AcceptTcpClientAsync(_stopping.Token);
                _ = Task.Run(async () =>
                {
                    using (accepted)
                    {
                        using var heard = new MemoryStream();
                        await accepted.GetStream().CopyToAsync(heard, _stopping.Token);
                        await accepted.GetStream().WriteAsync(heard.ToArray(), _stopping.Token);
                    }
                });
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The host behind the proxy: sends back whatever reaches it.</summary>
    private async Task EchoAsync()
    {
        try
        {
            while (true)
            {
                TcpClient accepted = await _upstream.AcceptTcpClientAsync(_stopping.Token);
                _ = Task.Run(async () =>
                {
                    using (accepted)
                        await accepted.GetStream().CopyToAsync(accepted.GetStream(), _stopping.Token);
                });
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }
}
