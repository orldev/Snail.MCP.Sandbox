namespace Snail.MCP.Sandbox.Configuration;

/// <summary>Everything one run may use, and where the code runs.</summary>
/// <remarks>
/// Each limit stands between one piece of code a model wrote and the node every other run shares. The
/// node is chosen to hold nothing worth taking; the limits are what keep one run from taking the node.
/// </remarks>
public sealed class SandboxOptions
{
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

    /// <summary>Image a .NET project is built and tested in: Microsoft's own SDK image, not one built for the sandbox.</summary>
    public string DotnetImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    /// <summary>Image a Node project is built and tested in: the official slim image.</summary>
    /// <remarks>A Python project is tested in <see cref="Image"/>, which carries pytest, so the node holds no Python image twice.</remarks>
    public string NodeImage { get; set; } = "node:22-bookworm-slim";

    /// <summary>Docker network the restore stage of a test run fetches packages over; none keeps even the restore offline.</summary>
    /// <remarks>
    /// The network is disconnected before the build, so project code that runs at build or test time never has one.
    /// Restore runs code too — MSBuild targets, npm install scripts, a setup.py — which is why a deployment points this
    /// at a network that reaches only a package mirror. With none, a run restores only what the package volume holds.
    /// </remarks>
    public string RestoreNetwork { get; set; } = "none";

    /// <summary>The restore proxy's address as a restore container reaches it, such as http://mcp-sandbox:3128; null keeps the proxy off.</summary>
    /// <remarks>
    /// The restore is handed it in its environment with the run's own name as credentials, and every host it asks for is
    /// answered in the tool's result. Put the restore on a network that reaches nothing but this address, and the list is
    /// exact and the proxy is the restore's only way out; on an open network, a program that ignores it goes around it.
    /// </remarks>
    public string? RestoreProxy { get; set; }

    /// <summary>Port the restore proxy listens on inside the server; 0 keeps it off.</summary>
    public int RestoreProxyPort { get; set; }

    /// <summary>Prefix of the volumes packages are kept in between test runs, one per toolchain.</summary>
    public string CacheVolumePrefix { get; set; } = "snail-sandbox-cache";

    /// <summary>Memory one test run may use, in bytes, swap included; a build needs more than a script.</summary>
    public long TestMemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>CPUs one test run may use.</summary>
    public double TestCpus { get; set; } = 2.0;

    /// <summary>Processes and threads one test run may hold at once; a compiler and a test host each run many threads.</summary>
    public long TestPids { get; set; } = 1024;

    /// <summary>Bytes a test run may write under /tmp, where the SDKs keep their first-run files.</summary>
    public long TestTmpFolderBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>Test runs the node executes at once, apart from program runs.</summary>
    public int MaxConcurrentTestRuns { get; set; } = 2;

    /// <summary>Seconds a test run gets when the caller names none, restore and build included.</summary>
    public int DefaultTestTimeoutSeconds { get; set; } = 600;

    /// <summary>Most seconds any caller may ask for a test run.</summary>
    public int MaxTestTimeoutSeconds { get; set; } = 1800;

    /// <summary>Most bytes a project may unpack to.</summary>
    public long MaxProjectBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>Most files a project may hold.</summary>
    public int MaxProjectFiles { get; set; } = 20000;

    /// <summary>Folder the inputs of each run are downloaded under, on the node's own disk.</summary>
    public string WorkRoot { get; set; } = Path.Combine(Path.GetTempPath(), "snail-sandbox");
}
