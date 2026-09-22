using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Adapters;

public sealed class ConfinementTests
{
    private static readonly SandboxOptions Limits = new()
    {
        Runtime = "runsc",
        User = "1000:1001",
        MemoryBytes = 1024 * 1024 * 1024,
        Cpus = 0.5,
        Pids = 64,
        OutFolderBytes = 1024 * 1024,
        TmpFolderBytes = 2 * 1024 * 1024
    };

    [Fact]
    public void Container_AnyRun_HasNoNetwork()
    {
        var container = Confinement.Container(Limits, TimeSpan.FromSeconds(30));

        Assert.Equal("none", container.HostConfig!.NetworkMode);
        Assert.True(container.NetworkDisabled);
    }

    [Fact]
    public void Container_AnyRun_CannotWriteItsRootNorGainAPrivilege()
    {
        var host = Confinement.Container(Limits, TimeSpan.FromSeconds(30)).HostConfig!;

        Assert.True(host.ReadonlyRootfs);
        Assert.Equal(["ALL"], host.CapDrop);
        Assert.Contains("no-new-privileges", host.SecurityOpt);
    }

    [Fact]
    public void Container_ConfiguredRuntime_IsAskedForByName() =>
        Assert.Equal("runsc", Confinement.Container(Limits, TimeSpan.FromSeconds(30)).HostConfig!.Runtime);

    /// <summary>Swap equal to memory means no swap: a run past its memory is killed, not slowed onto the node's disk.</summary>
    [Fact]
    public void Container_ConfiguredLimits_AreEachApplied()
    {
        var host = Confinement.Container(Limits, TimeSpan.FromSeconds(30)).HostConfig!;

        Assert.Equal(Limits.MemoryBytes, host.Memory);
        Assert.Equal(Limits.MemoryBytes, host.MemorySwap);
        Assert.Equal(500_000_000, host.NanoCPUs);
        Assert.Equal(64, host.PidsLimit);
        Assert.Contains("size=2097152", host.Tmpfs!["/tmp"], StringComparison.Ordinal);
        Assert.Contains("size=1048576,", OutOptions(), StringComparison.Ordinal);
    }

    [Fact]
    public void Container_OutFolder_BelongsToTheRunUserAlone() =>
        Assert.Contains("uid=1000,gid=1001,mode=0700", OutOptions(), StringComparison.Ordinal);

    /// <summary>A server that dies mid-run removes nothing; the container's own clock and auto-removal do.</summary>
    [Fact]
    public void Container_ServerGoneMidRun_StopsAndRemovesItselfAfterTheGrace()
    {
        var container = Confinement.Container(Limits, TimeSpan.FromSeconds(30));

        Assert.True(container.HostConfig!.AutoRemove);
        Assert.Contains($"time.sleep({30 + Confinement.Grace.TotalSeconds})", container.Cmd[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Execution_AnyRun_RunsTheCodeAsTheRunUserAmongItsFiles()
    {
        var execution = Confinement.Execution(Limits);

        Assert.Equal("1000:1001", execution.User);
        Assert.Equal("/sandbox/files", execution.WorkingDir);
        Assert.Equal(["python", "/sandbox/code/main.py"], execution.Cmd);
    }

    private static string OutOptions() =>
        Confinement.Container(Limits, TimeSpan.FromSeconds(30)).HostConfig!.Mounts!
            .Single(mount => mount.Target == Confinement.OutFolder)
            .VolumeOptions!.DriverConfig!.Options!["o"];
}
