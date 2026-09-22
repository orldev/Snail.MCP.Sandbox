using System.Globalization;
using Docker.DotNet.Models;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>The container one run gets: what it can reach, where it can write and how much it can use.</summary>
/// <remarks>
/// Measured on Docker 29 with runc before this was written: files copied into a created container's disk
/// volume survive the start; root-owned read-only entries stay unwritable for a non-root user with every
/// capability dropped; a tmpfs-backed volume stops a write at its size with ENOSPC; and such a volume, unlike
/// a plain --tmpfs mount, can be read back through the archive API while the container is alive. That last
/// fact is why the container idles and the code runs as an exec: /out has to be read before anything stops.
/// </remarks>
internal static class Confinement
{
    public const string Label = "snail.sandbox";

    public const string Root = "/sandbox";

    public const string CodeFolder = "code";

    public const string FilesFolder = "files";

    public const string CodeName = "main.py";

    public const string OutFolder = "/out";

    /// <summary>How long the container outlives the run it was made for before it stops, and removes, itself.</summary>
    /// <remarks>
    /// The server removes every container it starts; this clock covers the server dying mid-run, so a
    /// container nobody will ever remove still ends, and auto-removal takes its volumes with it.
    /// </remarks>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    public static ContainerRemoveParameters Removal => new() { Force = true, RemoveVolumes = true };

    /// <summary>Every container this server has made, whatever state it was left in.</summary>
    public static ContainersListParameters Leftovers => new()
    {
        All = true,
        Filters = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [Label] = true }
        }
    };

    public static CreateContainerParameters Container(SandboxOptions options, TimeSpan timeout) => new()
    {
        Image = options.Image,
        User = options.User,
        Hostname = "sandbox",
        NetworkDisabled = true,
        Cmd = ["python", "-c", $"import time; time.sleep({Math.Ceiling((timeout + Grace).TotalSeconds).ToString(CultureInfo.InvariantCulture)})"],
        Labels = new Dictionary<string, string> { [Label] = "run" },
        HostConfig = new HostConfig
        {
            Runtime = options.Runtime,
            NetworkMode = "none",
            ReadonlyRootfs = true,
            CapDrop = ["ALL"],
            SecurityOpt = ["no-new-privileges"],
            Memory = options.MemoryBytes,
            MemorySwap = options.MemoryBytes,
            NanoCPUs = (long)(options.Cpus * 1_000_000_000),
            PidsLimit = options.Pids,
            AutoRemove = true,
            LogConfig = new LogConfig { Type = "none", Config = new Dictionary<string, string>() },
            Tmpfs = new Dictionary<string, string> { ["/tmp"] = $"rw,nosuid,nodev,size={options.TmpFolderBytes}" },
            Mounts = [Inputs(), Outputs(options)]
        }
    };

    public static ContainerExecCreateParameters Execution(SandboxOptions options) => new()
    {
        User = options.User,
        WorkingDir = $"{Root}/{FilesFolder}",
        Cmd = ["python", $"{Root}/{CodeFolder}/{CodeName}"],
        Env = ["HOME=/tmp", "MPLCONFIGDIR=/tmp/matplotlib", "PYTHONDONTWRITEBYTECODE=1", "PYTHONUNBUFFERED=1"],
        AttachStdout = true,
        AttachStderr = true
    };

    /// <summary>A disk volume for the code and its inputs, filled before the start and never copied from the image.</summary>
    /// <remarks>
    /// Mounted writable because the archive API refuses a read-only target; the code still cannot write to it,
    /// since every entry is owned by root and the run user holds no capability that overrides that.
    /// </remarks>
    private static Mount Inputs() => new()
    {
        Type = "volume",
        Target = Root,
        VolumeOptions = new VolumeOptions { NoCopy = true }
    };

    /// <summary>A tmpfs volume of fixed size, owned by the run user, for the files the code leaves behind.</summary>
    private static Mount Outputs(SandboxOptions options)
    {
        var owner = options.User.Split(':');

        return new Mount
        {
            Type = "volume",
            Target = OutFolder,
            VolumeOptions = new VolumeOptions
            {
                NoCopy = true,
                DriverConfig = new Driver
                {
                    Name = "local",
                    Options = new Dictionary<string, string>
                    {
                        ["type"] = "tmpfs",
                        ["device"] = "tmpfs",
                        ["o"] = $"size={options.OutFolderBytes},uid={owner[0]},gid={owner[1]},mode=0700,nosuid,nodev,noexec"
                    }
                }
            }
        };
    }
}
