using System.Formats.Tar;
using System.IO.Compression;
using Snail.MCP.Sandbox.Application;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>The project a test run builds, rewritten from the archive it arrived in into the one the container is filled from.</summary>
/// <remarks>
/// Docker keeps the owner an archive entry names, and a build writes beside its sources — obj, bin, __pycache__,
/// node_modules — so every entry is rewritten as the run user's, with a directory entry for every folder a file sits
/// in: a folder the archive only implied would be made by the daemon as root's, and the build would stop at the first
/// file it writes there. Links and special files are left out and named, a path that climbs out of the project is
/// refused, and the declared sizes are added up before a byte is copied, so an archive that unpacks to more than the
/// limit is stopped at its headers.
/// </remarks>
internal static class ProjectArchive
{
    public const string Folder = "project";

    public const string DependenciesFolder = "deps";

    private const string Hint = "Pass the project as a tar or tar.gz of its working folder, without build outputs and dependencies.";

    private const UnixFileMode Listable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode Readable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode Executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Reads <paramref name="source"/>, a tar or a gzipped tar, and writes <paramref name="target"/> for the container.</summary>
    public static async Task<Answer<PackedProject>> RepackAsync(string source, string target, Owner owner, ProjectLimits limits, CancellationToken cancellationToken)
    {
        await using var input = await OpenAsync(source, cancellationToken);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using var reader = new TarReader(input, leaveOpen: true);
        await using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);

        var packing = new Packing(writer, owner);
        await packing.FolderAsync(Folder, cancellationToken);
        await packing.FolderAsync(DependenciesFolder, cancellationToken);

        try
        {
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (await packing.AddAsync(entry, limits, cancellationToken) is { } refusal)
                    return refusal;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or EndOfStreamException)
        {
            return Answer.Failed($"The project archive could not be read: {exception.Message}", Hint, FailureTypes.Malformed);
        }

        return Answer.Ok(new PackedProject(target, [.. packing.Paths], [.. packing.Omitted]));
    }

    /// <summary>The archive's bytes, unzipped on the way when its first two bytes are gzip's.</summary>
    private static async Task<Stream> OpenAsync(string source, CancellationToken cancellationToken)
    {
        var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var magic = new byte[2];
        var read = await file.ReadAtLeastAsync(magic, 2, throwOnEndOfStream: false, cancellationToken);
        file.Position = 0;

        return read == 2 && magic[0] == 0x1f && magic[1] == 0x8b ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    /// <summary>The path below the project's root, or null when it is empty or climbs out of it.</summary>
    internal static string? Inside(string entryName)
    {
        var trimmed = entryName.Replace('\\', '/').TrimEnd('/');

        while (trimmed.StartsWith("./", StringComparison.Ordinal))
            trimmed = trimmed[2..];

        if (trimmed.Length == 0 || trimmed == ".")
            return null;

        var segments = trimmed.Split('/');

        return segments.Any(segment => segment is "" or "." or "..") ? null : trimmed;
    }

    /// <summary>The entries written so far, the folders already made and what was left out.</summary>
    private sealed class Packing(TarWriter writer, Owner owner)
    {
        private readonly HashSet<string> _folders = new(StringComparer.Ordinal);

        private long _bytes;

        public List<string> Paths { get; } = [];

        public List<string> Omitted { get; } = [];

        /// <summary>Copies one entry, or answers why the whole project is refused.</summary>
        public async Task<AnswerFailure?> AddAsync(TarEntry entry, ProjectLimits limits, CancellationToken cancellationToken)
        {
            if (Inside(entry.Name) is not { } path)
                return entry.Name.Trim('/', '.').Length == 0 ? null : Answer.Failed($"The archive entry {entry.Name} points outside the project folder.", Hint, FailureTypes.Malformed);

            if (entry.EntryType is TarEntryType.Directory)
            {
                await FoldersAsync(path, cancellationToken);
                return null;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
            {
                Omitted.Add($"{path}: a link or a special file, not a regular file");
                return null;
            }

            _bytes += entry.Length;
            if (_bytes > limits.MaxBytes)
                return Answer.Failed($"The project unpacks to more than {limits.MaxBytes / (1024 * 1024)} MB.", Hint, FailureTypes.TooLarge);

            if (Paths.Count >= limits.MaxFiles)
                return Answer.Failed($"The project has more than {limits.MaxFiles} files.", Hint, FailureTypes.TooLarge);

            await FoldersAsync(ParentOf(path), cancellationToken);
            await writer.WriteEntryAsync(File($"{Folder}/{path}", entry), cancellationToken);
            Paths.Add(path);

            return null;
        }

        public async Task FolderAsync(string name, CancellationToken cancellationToken)
        {
            if (_folders.Add(name))
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, $"{name}/") { Mode = Listable, Uid = owner.Uid, Gid = owner.Gid }, cancellationToken);
        }

        /// <summary>Makes the folder and every folder above it that is not made yet, outermost first.</summary>
        private async Task FoldersAsync(string? path, CancellationToken cancellationToken)
        {
            if (path is null)
                return;

            var segments = path.Split('/');

            for (var depth = 1; depth <= segments.Length; depth++)
                await FolderAsync($"{Folder}/{string.Join('/', segments.Take(depth))}", cancellationToken);
        }

        private PaxTarEntry File(string name, TarEntry source) => new(TarEntryType.RegularFile, name)
        {
            DataStream = source.DataStream,
            Mode = (source.Mode & Executable) != 0 ? Readable | Executable : Readable,
            Uid = owner.Uid,
            Gid = owner.Gid
        };

        private static string? ParentOf(string path)
        {
            var slash = path.LastIndexOf('/');

            return slash < 0 ? null : path[..slash];
        }
    }
}

/// <summary>The numeric user and group every entry of the project is written as.</summary>
internal sealed record Owner(int Uid, int Gid)
{
    /// <summary>The owner of a uid:gid setting, already validated as numeric.</summary>
    public static Owner Of(string user)
    {
        var parts = user.Split(':');

        return new Owner(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>How large a project may unpack to, in bytes and in files.</summary>
internal sealed record ProjectLimits(long MaxBytes, int MaxFiles);
