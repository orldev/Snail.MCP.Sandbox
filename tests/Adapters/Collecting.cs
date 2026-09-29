using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Snail.MCP.Sandbox.Tests.Adapters;

/// <summary>Scripts the exec that reads /out back: tar as the run user, its archive framed on the exec's standard output.</summary>
public static class Collecting
{
    /// <summary>What <see cref="Daemon.Calls"/> names every step of reading /out back.</summary>
    public const string Collect = "Collect";

    private const string ExecId = "collect-1";

    /// <summary>tar archives /out, says <paramref name="warning"/> on its standard error first, and ends with <paramref name="exitCode"/>.</summary>
    public static Daemon Collects(this Daemon daemon, Stream archive, long exitCode = 0, string warning = "") => daemon
        .Answer("CreateContainerExecAsync", IsCollection, Collect, _ => Task.FromResult(new ContainerExecCreateResponse { ID = ExecId }))
        .Answer("StartContainerExecAsync", IsCollecting, Collect, _ => Task.FromResult(Framed(archive, warning)))
        .Answer("InspectContainerExecAsync", IsCollecting, Collect, _ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = exitCode }));

    /// <summary>The container is gone by the time /out is read, as a gVisor sandbox killed whole removes itself.</summary>
    public static Daemon CollectsFromAGoneContainer(this Daemon daemon) => daemon
        .Answer("CreateContainerExecAsync", IsCollection, Collect, _ => Task.FromException<ContainerExecCreateResponse>(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "No such container")));

    private static bool IsCollection(object?[] arguments) =>
        arguments[1] is ContainerExecCreateParameters { Cmd: ["tar", ..] };

    private static bool IsCollecting(object?[] arguments) => arguments[0] as string == ExecId;

    /// <summary>The archive as the daemon frames an exec's output: a stream byte, three zeros, a big-endian length.</summary>
    private static MultiplexedStream Framed(Stream archive, string warning)
    {
        using var content = new MemoryStream();
        archive.CopyTo(content);

        var framed = new MemoryStream();
        if (warning.Length > 0)
            Frame(framed, 2, System.Text.Encoding.UTF8.GetBytes(warning));

        Frame(framed, 1, content.ToArray());
        framed.Position = 0;

        return new MultiplexedStream(framed, multiplexed: true);
    }

    private static void Frame(Stream framed, byte stream, byte[] payload)
    {
        framed.Write([stream, 0, 0, 0, .. BitConverter.GetBytes(payload.Length).Reverse()]);
        framed.Write(payload);
    }
}
