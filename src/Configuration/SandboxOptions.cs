namespace Snail.MCP.Sandbox.Configuration;

/// <summary>Everything one run may use, and where the code runs.</summary>
/// <remarks>
/// Each limit stands between one piece of code a model wrote and the node every other run shares. The
/// node is chosen to hold nothing worth taking; the limits are what keep one run from taking the node.
/// </remarks>
public sealed class SandboxOptions
{
    public const string SectionName = "Snail";

    /// <summary>Container runtime the code runs under; the daemon must know it, or the server refuses to run anything.</summary>
    /// <remarks>
    /// runsc is gVisor: a kernel in user space between the code and the host's own. Falling back to the default
    /// runtime when it is missing would keep the server working and quietly remove the one layer that stops a
    /// kernel exploit, so a missing runtime is a refusal instead.
    /// </remarks>
    public string Runtime { get; set; } = "runsc";

    /// <summary>Docker Engine API the server starts containers through; on a sandbox node, the node's own daemon.</summary>
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";

    /// <summary>Image the code runs in: Python with the analysis libraries, and nothing that talks to a network.</summary>
    public string Image { get; set; } = "snail/sandbox-python:latest";

    /// <summary>User the code runs as inside the container, as numeric uid:gid; never root.</summary>
    /// <remarks>Numeric because /out is a tmpfs mounted for this owner, and a mount takes numbers, not names.</remarks>
    public string User { get; set; } = "65534:65534";

    /// <summary>Memory one run may use, in bytes, swap included.</summary>
    public long MemoryBytes { get; set; } = 512 * 1024 * 1024;

    /// <summary>CPUs one run may use.</summary>
    public double Cpus { get; set; } = 1.0;

    /// <summary>Processes and threads one run may hold at once; a fork bomb stops here.</summary>
    public long Pids { get; set; } = 128;

    /// <summary>Runs the node executes at once; a call past it waits for a place rather than being refused.</summary>
    /// <remarks>Every other limit is per run, so without this one twenty calls at once still take the whole node.</remarks>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>Seconds a run gets when the caller names none.</summary>
    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>Most seconds any caller may ask for.</summary>
    public int MaxTimeoutSeconds { get; set; } = 300;

    /// <summary>Bytes of standard output and of standard error each kept from one run.</summary>
    public int OutputLimitBytes { get; set; } = 65536;

    /// <summary>Bytes the code may write under /out.</summary>
    public long OutFolderBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Bytes the code may write under /tmp, where Python libraries keep their caches.</summary>
    public long TmpFolderBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Most files returned from /out.</summary>
    public int MaxOutputFiles { get; set; } = 10;

    /// <summary>Most input files one run may be given.</summary>
    public int MaxInputFiles { get; set; } = 20;

    /// <summary>Largest single input file, in bytes.</summary>
    public long MaxInputBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Largest total of all input files of one run, in bytes.</summary>
    public long MaxInputTotalBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>Hosts input links may point at, as host or host:port; empty refuses every link.</summary>
    /// <remarks>
    /// The server downloads what it is given, so an open list would let a model turn it into a fetcher of
    /// anything the node can reach — the cluster's own services first. Only the object store is named here.
    /// </remarks>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>Folder the inputs of each run are downloaded under, on the node's own disk.</summary>
    public string WorkRoot { get; set; } = Path.Combine(Path.GetTempPath(), "snail-sandbox");
}
