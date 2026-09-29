using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

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

    /// <summary>Measured on runsc release-20260921.0: the pids limit let 500 threads start, RLIMIT_NPROC stopped them at 63.</summary>
    [Fact]
    public void Container_UnderGvisor_HasItsProcessesCountedByTheSandboxKernel()
    {
        var limit = Assert.Single(Confinement.Container(Limits, TimeSpan.FromSeconds(30)).HostConfig!.Ulimits);

        Assert.Equal("nproc", limit.Name);
        Assert.Equal(64, limit.Soft);
        Assert.Equal(64, limit.Hard);
    }

    /// <summary>Under runc RLIMIT_NPROC counts the run user's processes across the node, so concurrent runs would share one limit.</summary>
    [Fact]
    public void Container_UnderRunc_LeavesProcessesToTheCgroup()
    {
        var limits = new SandboxOptions { Runtime = "runc", User = Limits.User, Pids = 64 };

        Assert.Empty(Confinement.Container(limits, TimeSpan.FromSeconds(30)).HostConfig!.Ulimits);
    }

    /// <summary>A test run's container is counted the same way, with the larger limit a build needs.</summary>
    [Fact]
    public void TestContainer_UnderGvisor_HasItsProcessesCountedByTheSandboxKernel()
    {
        var limits = new SandboxOptions { Runtime = Confinement.Gvisor, User = Limits.User, TestPids = 512 };

        var limit = Assert.Single(TestCommands.Container(Toolchain.Python, limits, TimeSpan.FromSeconds(30)).HostConfig!.Ulimits);

        Assert.Equal(512, limit.Hard);
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

    /// <summary>
    /// Measured on runsc release-20260921.0: a tmpfs made inside the container refuses to execute a copied binary, where
    /// a Docker volume on a host tmpfs let it run. Both places the code can write are that kind of tmpfs.
    /// </summary>
    [Theory]
    [InlineData("/tmp")]
    [InlineData("/out")]
    public void Container_AnyFolderTheCodeWrites_IsATmpfsThatExecutesNothing(string folder) =>
        Assert.Contains("noexec", Confinement.Container(Limits, TimeSpan.FromSeconds(30)).HostConfig!.Tmpfs![folder].Split(','));

    /// <summary>/out is a tmpfs of the container itself, not a volume: only such a tmpfs keeps noexec under gVisor.</summary>
    [Fact]
    public void Container_OutFolder_IsNoVolume() =>
        Assert.DoesNotContain(Confinement.Container(Limits, TimeSpan.FromSeconds(30)).HostConfig!.Mounts!, mount => mount.Target == Confinement.OutFolder);

    /// <summary>/out belongs to the run user alone, so tar reads it back as that user; links are archived as links, never followed.</summary>
    [Fact]
    public void Collection_AnyRun_ArchivesOutAsTheRunUser()
    {
        var collection = Confinement.Collection(Limits);

        Assert.Equal(Limits.User, collection.User);
        Assert.Equal(["tar", "-C", "/", "-cf", "-", "out"], collection.Cmd);
        Assert.DoesNotContain("-h", collection.Cmd);
    }

    private static string OutOptions() => Confinement.Container(Limits, TimeSpan.FromSeconds(30)).HostConfig!.Tmpfs![Confinement.OutFolder];
}
