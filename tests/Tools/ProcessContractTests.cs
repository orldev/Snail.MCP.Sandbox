using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>
/// The program as the platform starts it — the build beside the tests, or the publication <c>SNAIL_SANDBOX_SERVER</c>
/// names — asked over HTTP through every tool once.
/// </summary>
/// <remarks>
/// The server has no stdio transport, so the contract is the HTTP one the node serves. The Docker endpoint names a socket
/// that does not exist: no container is made or swept, and the tools that need the daemon have to say so in an answer
/// of their own rather than fail the call.
/// </remarks>
public sealed class ProcessContractTests : IAsyncLifetime
{
    public const string ServerVariable = "SNAIL_SANDBOX_SERVER";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string _workRoot = TestFolder.Create();
    private readonly Uri _address = new($"http://127.0.0.1:{FreePort()}");
    private Process _server = default!;

    public async Task InitializeAsync()
    {
        var (command, arguments) = ServerProgram.Command("Snail.MCP.Sandbox", ServerVariable);
        var start = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        start.Environment["SNAIL_MCP_SANDBOX_URL"] = _address.ToString();
        start.Environment["SNAIL_MCP_SANDBOX_DOCKER_ENDPOINT"] = $"unix://{Path.Combine(_workRoot, "docker.sock")}";
        start.Environment["SNAIL_MCP_SANDBOX_WORK_ROOT"] = _workRoot;
        start.Environment[ServerSettings.FileVariableOf("sandbox")] = ServerProgram.NoSettingsFile;

        _server = Process.Start(start)!;
        _server.OutputDataReceived += (_, _) => { };
        _server.ErrorDataReceived += (_, _) => { };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        using var waiting = new CancellationTokenSource(Timeout);
        using var http = new HttpClient { BaseAddress = _address };

        while (true)
        {
            try
            {
                using var answer = await http.GetAsync(new Uri("/healthz", UriKind.Relative), waiting.Token);

                if (answer.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) when (!_server.HasExited)
            {
            }

            await Task.Delay(100, waiting.Token);
        }
    }

    public Task DisposeAsync()
    {
        _server.Kill(entireProcessTree: true);
        _server.WaitForExit();
        _server.Dispose();
        Directory.Delete(_workRoot, recursive: true);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ListTools_StartedProgram_OffersEveryTool()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var tools = await session.ListToolsAsync(cancellationToken: cancellation.Token);

        Assert.Equal(["find_tool", "sandbox_diagnose", "sandbox_run_python", "sandbox_run_tests"], tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The guard rules of every Snail server, on the list a model receives: names in snake case and listed once, the tool
    /// and every parameter described, read-only or destructive stated, every allowed value named, English.
    /// </summary>
    [Fact]
    public async Task Tools_AsListed_KeepTheGuardRules()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var tools = await session.ListToolsAsync(cancellationToken: cancellation.Token);

        Assert.Empty(ToolGuards.Problems(tools.Select(tool => tool.ProtocolTool)));
    }

    [Fact]
    public async Task CallTool_FindTool_NamesTheToolForATask()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var result = await session.CallToolAsync("find_tool", new Dictionary<string, object?> { ["task"] = "draw a chart from this table" }, cancellationToken: cancellation.Token);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("sandbox_run_python", result.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_DiagnoseWithoutADaemon_NamesTheEndpointThatDidNotAnswer()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var result = await session.CallToolAsync("sandbox_diagnose", cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        Assert.Contains("did not answer", result.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RunPythonWithoutADaemon_IsAToolErrorRatherThanAFailedCall()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var result = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print('rows: 12')" }, cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        Assert.Contains("could not run the code", result.Text(), StringComparison.Ordinal);
    }

    /// <summary>No host is allowed by default, so the link is refused before anything is downloaded or a container exists.</summary>
    [Fact]
    public async Task CallTool_RunTestsFromAHostNotAllowed_IsRefusedByName()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var result = await session.CallToolAsync("sandbox_run_tests", new Dictionary<string, object?> { ["project"] = "https://example.com/project.tar.gz" }, cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        Assert.Contains("example.com", result.Text(), StringComparison.Ordinal);
    }

    private Task<McpClient> SessionAsync(CancellationToken cancellationToken) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_address, "/mcp") }), cancellationToken: cancellationToken);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
