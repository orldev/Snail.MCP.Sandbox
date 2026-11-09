using System.Diagnostics;
using ModelContextProtocol.Client;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Sandbox.Tests.Tools;

/// <summary>
/// The program as the platform starts it — the build beside the tests, or the publication <c>SNAIL_SANDBOX_SERVER</c>
/// names — asked over HTTP through every tool once.
/// </summary>
/// <remarks>
/// The server has no stdio transport, so the contract is the HTTP one the node serves, started by Snail.Sdk.Mcp.Testing on
/// a free loopback port and ready once /healthz answers. The Docker endpoint names a socket that does not exist: no
/// container is made or swept, and the tools that need the daemon have to say so in an answer of their own rather than
/// fail the call.
/// </remarks>
public sealed class ProcessContractTests : IAsyncLifetime
{
    public const string ServerVariable = "SNAIL_SANDBOX_SERVER";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string _workRoot = TestFolder.Create();
    private HttpServerProgram _server = default!;

    public async Task InitializeAsync() =>
        _server = await HttpServerProgram.StartAsync("Snail.MCP.Sandbox", ServerVariable, new Dictionary<string, string?>
        {
            ["SNAIL_MCP_SANDBOX_DOCKER_ENDPOINT"] = $"unix://{Path.Combine(_workRoot, "docker.sock")}",
            ["SNAIL_MCP_SANDBOX_WORK_ROOT"] = _workRoot
        });

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_workRoot, recursive: true);
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

    /// <summary>A page in a browser always sends an Origin and an agent's client never does: the refusal is Snail.Sdk.Mcp.Http's, kept by taking its host.</summary>
    [Fact]
    public async Task Endpoint_AsServed_RefusesBrowsersOnly() =>
        Assert.Empty(await HttpGuards.ProblemsAsync(_server.Endpoint));

    [Fact]
    public async Task CallTool_FindTool_NamesTheToolForATask()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var result = await session.CallToolAsync("find_tool", new Dictionary<string, object?> { ["task"] = "draw a chart from this table" }, cancellationToken: cancellation.Token);

        Assert.True(result.IsOk(), result.Text());
        Assert.Contains("sandbox_run_python", result.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_DiagnoseWithoutADaemon_NamesTheEndpointThatDidNotAnswer()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var result = await session.CallToolAsync("sandbox_diagnose", cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        Assert.Contains("did not answer", result.Error(), StringComparison.Ordinal);
        Assert.Equal("notReady", result.Type());
    }

    [Fact]
    public async Task CallTool_RunPythonWithoutADaemon_IsAToolErrorRatherThanAFailedCall()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var result = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print('rows: 12')" }, cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        Assert.Contains("could not run the code", result.Error(), StringComparison.Ordinal);
        Assert.Equal(ToolFailures.Internal, result.Type());
    }

    /// <summary>No host is allowed by default, so the link is refused before anything is downloaded or a container exists.</summary>
    [Fact]
    public async Task CallTool_RunTestsFromAHostNotAllowed_IsRefusedByName()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var result = await session.CallToolAsync("sandbox_run_tests", new Dictionary<string, object?> { ["project"] = "https://example.com/project.tar.gz" }, cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        Assert.Contains("example.com", result.Error(), StringComparison.Ordinal);
        Assert.Equal("notAllowed", result.Type());
    }

    /// <summary>A wait outside the range is the call's mistake, the SDK's <c>arguments</c>, refused before a job exists.</summary>
    [Fact]
    public async Task CallTool_WaitOutsideTheRange_IsTypedArguments()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(cancellation.Token);

        var result = await session.CallToolAsync("sandbox_run_python", new Dictionary<string, object?> { ["code"] = "print(1)", ["wait"] = -1 }, cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        Assert.Equal(ToolFailures.Arguments, result.Type());
    }

    /// <summary>
    /// The server holds no key, so on a machine it keeps to loopback (Snail.Sdk.Mcp's reach <c>LoopbackOrContainer</c>):
    /// an address at every interface outside a container stops the program before it listens, and says which variable
    /// asked for it — in one line with exit code 1, not as a crash with a trace. In the image, whose published port is the
    /// boundary, every interface is the reading.
    /// </summary>
    [Fact]
    public async Task Start_EveryInterfaceOutsideAContainer_StopsNamingTheVariable()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        var (program, arguments) = ServerProgram.Command("Snail.MCP.Sandbox", ServerVariable);
        var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        start.Environment["HOME"] = ServerProgram.Home;
        start.Environment["SNAIL_MCP_SANDBOX_CONFIG"] = ServerProgram.NoSettingsFile;
        start.Environment["SNAIL_MCP_SANDBOX_URL"] = $"http://0.0.0.0:{Ports.Free()}/";
        start.Environment.Remove("DOTNET_RUNNING_IN_CONTAINER");

        using var started = Process.Start(start)!;
        var said = started.StandardError.ReadToEndAsync(cancellation.Token);
        await started.WaitForExitAsync(cancellation.Token);

        var stderr = await said;
        Assert.Equal(1, started.ExitCode);
        Assert.Contains("SNAIL_MCP_SANDBOX_URL", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
    }

    private Task<McpClient> SessionAsync(CancellationToken cancellationToken) => _server.SessionAsync(cancellationToken: cancellationToken);
}
