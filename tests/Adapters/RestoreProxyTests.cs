using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Adapters;

/// <summary>
/// The restore reaches the network through the proxy, which writes down every host a watched run asked for and refuses
/// a request that names no run it watches. Real sockets: a proxy is its bytes on the wire.
/// </summary>
public sealed class RestoreProxyTests : IAsyncLifetime
{
    private readonly TcpListener _upstream = new(IPAddress.Loopback, 0);
    private RestoreProxy _proxy = default!;
    private readonly CancellationTokenSource _stopping = new();

    private int UpstreamPort => ((IPEndPoint)_upstream.LocalEndpoint).Port;

    public async Task InitializeAsync()
    {
        _upstream.Start();
        _ = EchoAsync();

        int port = Ports.Free();
        _proxy = new RestoreProxy(Options.Create(new SandboxOptions { RestoreProxy = $"http://127.0.0.1:{port}", RestoreProxyPort = port, RestoreHosts = ["127.0.0.1", "localhost"] }), NullLogger<RestoreProxy>.Instance);
        await _proxy.StartAsync(_stopping.Token);

        for (var waited = 0; _proxy.Port == 0 && waited < 50; waited++)
            await Task.Delay(20);
    }

    public async Task DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _proxy.StopAsync(CancellationToken.None);
        _upstream.Stop();
    }

    [Fact]
    public async Task Connect_WatchedRun_TunnelsBothWaysAndNamesTheHost()
    {
        using RestoreWatch watch = _proxy.Watch()!;

        using var client = await ConnectedAsync();
        string answer = await AskAsync(client, $"CONNECT 127.0.0.1:{UpstreamPort} HTTP/1.1\r\nHost: 127.0.0.1\r\nProxy-Authorization: Basic {Credentials(watch)}\r\n\r\n");
        await client.GetStream().WriteAsync("ping"u8.ToArray());
        string echoed = await ReadAsync(client, 4);

        Assert.StartsWith("HTTP/1.1 200", answer, StringComparison.Ordinal);
        Assert.Equal("ping", echoed);
        Assert.Equal(["127.0.0.1"], watch.Hosts);
    }

    [Fact]
    public async Task Connect_NoRunNamed_IsRefusedAndNothingIsRecorded()
    {
        using RestoreWatch watch = _proxy.Watch()!;

        using var client = await ConnectedAsync();
        string answer = await AskAsync(client, $"CONNECT 127.0.0.1:{UpstreamPort} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 407", answer, StringComparison.Ordinal);
        Assert.Empty(watch.Hosts);
    }

    /// <summary>A run that ended is watched no more: its name opens nothing.</summary>
    [Fact]
    public async Task Connect_RunThatEnded_IsRefused()
    {
        RestoreWatch watch = _proxy.Watch()!;
        string credentials = Credentials(watch);
        watch.Dispose();

        using var client = await ConnectedAsync();
        string answer = await AskAsync(client, $"CONNECT 127.0.0.1:{UpstreamPort} HTTP/1.1\r\nProxy-Authorization: Basic {credentials}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 407", answer, StringComparison.Ordinal);
    }

    /// <summary>A package index on plain http is reached too: the host is named, and it gets the path, not the proxy's headers.</summary>
    [Fact]
    public async Task Get_AbsoluteAddress_ForwardsThePathWithoutTheProxysHeaders()
    {
        using RestoreWatch watch = _proxy.Watch()!;

        using var client = await ConnectedAsync();
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET http://localhost:{UpstreamPort}/simple/ HTTP/1.1\r\nHost: localhost\r\nProxy-Authorization: Basic {Credentials(watch)}\r\n\r\n"));
        string echoed = await ReadAsync(client, "GET /simple/ HTTP/1.1\r\nHost: localhost\r\n\r\n".Length);

        Assert.Equal("GET /simple/ HTTP/1.1\r\nHost: localhost\r\n\r\n", echoed);
        Assert.Equal(["localhost"], watch.Hosts);
    }

    /// <summary>A body that arrives in the same packet as the head belongs to the host: a head read a packet at a time must hand it on, not swallow it.</summary>
    [Fact]
    public async Task Post_BodyInTheSamePacketAsTheHead_ReachesTheHostWhole()
    {
        using RestoreWatch watch = _proxy.Watch()!;

        using var client = await ConnectedAsync();
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST http://localhost:{UpstreamPort}/upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\nProxy-Authorization: Basic {Credentials(watch)}\r\n\r\nhello"));
        string echoed = await ReadAsync(client, "POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\n\r\nhello".Length);

        Assert.EndsWith("\r\n\r\nhello", echoed, StringComparison.Ordinal);
    }

    /// <summary>A client that sends the first bytes of its handshake right behind CONNECT, before the 200, still has them reach the host.</summary>
    [Fact]
    public async Task Connect_FirstTunnelBytesBehindTheHead_ReachTheHost()
    {
        using RestoreWatch watch = _proxy.Watch()!;

        using var client = await ConnectedAsync();
        string answer = await AskAsync(client, $"CONNECT 127.0.0.1:{UpstreamPort} HTTP/1.1\r\nHost: 127.0.0.1\r\nProxy-Authorization: Basic {Credentials(watch)}\r\n\r\nearly");
        string echoed = await ReadAsync(client, 5);

        Assert.StartsWith("HTTP/1.1 200", answer, StringComparison.Ordinal);
        Assert.Equal("early", echoed);
    }

    [Fact]
    public async Task Watch_SameHostTwice_NamesItOnce()
    {
        using RestoreWatch watch = _proxy.Watch()!;

        for (var time = 0; time < 2; time++)
        {
            using var client = await ConnectedAsync();
            await AskAsync(client, $"CONNECT 127.0.0.1:{UpstreamPort} HTTP/1.1\r\nProxy-Authorization: Basic {Credentials(watch)}\r\n\r\n");
        }

        Assert.Equal(["127.0.0.1"], watch.Hosts);
    }

    [Fact]
    public void Watch_ProxyOff_WatchesNothing() =>
        Assert.Null(new RestoreProxy(Options.Create(new SandboxOptions()), NullLogger<RestoreProxy>.Instance).Watch());

    [Fact]
    public void Watch_Run_GivesTheAddressWithTheRunsName()
    {
        using RestoreWatch watch = _proxy.Watch()!;

        var address = new Uri(watch.Proxy);

        Assert.Equal("127.0.0.1", address.Host);
        Assert.Matches("^[0-9a-f]{32}:x$", address.UserInfo);
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

    private static async Task<string> ReadAsync(TcpClient client, int bytes)
    {
        var buffer = new byte[bytes];
        var read = 0;
        while (read < bytes)
            read += await client.GetStream().ReadAsync(buffer.AsMemory(read)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        return Encoding.ASCII.GetString(buffer);
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
