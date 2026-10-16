using System.Net;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;
using Snail.MCP.Sandbox.Tests.Adapters;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>The test-run adapter against a node that changes under it between two runs.</summary>
public sealed class DockerTestRunsChaosTests : IDisposable
{
    private readonly string _folder = TestFolder.Create();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// Breaks the cache: a package volume removed between two runs — docker volume prune takes every volume no container
    /// holds, and the README says removing one costs only the next download — used to come back as root's, and every
    /// restore of that toolchain failed on it until the server restarted, worded as the project's own packages being
    /// wrong. The volume is handed over again whenever it is gone, not once per process.
    /// </summary>
    [Fact]
    public async Task TestAsync_PackageVolumeGoneBetweenRuns_HandsItOverAgain()
    {
        var daemon = Restoring()
            .Answer("InspectAsync", _ => Task.FromException<VolumeResponse>(new DockerApiException(HttpStatusCode.NotFound, "no such volume")));
        var tests = new DockerTestRuns(daemon.Client, Options.Create(new SandboxOptions { WorkRoot = _folder }), NullLogger<DockerTestRuns>.Instance);

        await tests.TestAsync(await ProjectAsync(), Toolchain.Python, TimeSpan.FromSeconds(5), CancellationToken.None);
        await tests.TestAsync(await ProjectAsync(), Toolchain.Python, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(2, daemon.Calls.Count(call => call == "WaitContainerAsync"));
    }

    private async Task<PackedProject> ProjectAsync()
    {
        var archive = Path.Combine(_folder, $"{Guid.NewGuid():N}.tar");
        await File.WriteAllBytesAsync(archive, new byte[1024]);

        return new PackedProject(archive, ["test_a.py"], []);
    }

    /// <summary>A daemon with runsc on which the restore stage fails, so a run is the handover and one exec.</summary>
    private static Daemon Restoring() => new Daemon()
        .Answer("GetSystemInfoAsync", _ => Task.FromResult(new SystemInfoResponse { Runtimes = new Dictionary<string, RuntimeWithStatus> { ["runsc"] = new() } }))
        .Answer("CreateContainerAsync", _ => Task.FromResult(new CreateContainerResponse { ID = "test-1", Warnings = [] }))
        .Answer("StartContainerAsync", _ => Task.FromResult(true))
        .Answer("WaitContainerAsync", _ => Task.FromResult(new ContainerWaitResponse { StatusCode = 0 }))
        .Answer("ExtractArchiveToContainerAsync", _ => Task.CompletedTask)
        .Answer("CreateContainerExecAsync", _ => Task.FromResult(new ContainerExecCreateResponse { ID = "exec-1" }))
        .Answer("StartContainerExecAsync", _ => Task.FromResult(Framed("EACCES: /cache/pip\n")))
        .Answer("InspectContainerExecAsync", _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = 1 }))
        .Answer("RemoveContainerAsync", _ => Task.CompletedTask);

    private static MultiplexedStream Framed(string stdout)
    {
        var payload = Encoding.UTF8.GetBytes(stdout);
        var framed = new MemoryStream();
        framed.Write([1, 0, 0, 0, .. BitConverter.GetBytes(payload.Length).Reverse()]);
        framed.Write(payload);
        framed.Position = 0;

        return new MultiplexedStream(framed, multiplexed: true);
    }
}
