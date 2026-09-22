using System.Formats.Tar;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>What /out held after a run: the files returned, and the ones left out with the reason.</summary>
internal sealed record Collected(IReadOnlyList<OutputFile> Files, IReadOnlyList<string> Omitted);

/// <summary>The tar archives a run travels in: the code and inputs going in, the files of /out coming back.</summary>
internal static class RunArchive
{
    private const UnixFileMode Readable = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode Listable = Readable | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private static readonly FileExtensionContentTypeProvider MediaTypes = new();

    /// <summary>Writes the code and the inputs as root-owned, read-only entries, laid out as the container expects them.</summary>
    /// <remarks>The owner and mode are the whole of what keeps the inputs unwritable, so they are stated, not left to defaults.</remarks>
    public static async Task PackAsync(Stream target, string code, IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken)
    {
        await using var writer = new TarWriter(target, TarEntryFormat.Pax, leaveOpen: true);

        await writer.WriteEntryAsync(Folder(Confinement.CodeFolder), cancellationToken);
        await using (var source = new MemoryStream(Encoding.UTF8.GetBytes(code)))
            await writer.WriteEntryAsync(File($"{Confinement.CodeFolder}/{Confinement.CodeName}", source), cancellationToken);

        await writer.WriteEntryAsync(Folder(Confinement.FilesFolder), cancellationToken);
        foreach (var input in inputs)
        {
            await using var content = System.IO.File.OpenRead(input.Path);
            await writer.WriteEntryAsync(File($"{Confinement.FilesFolder}/{input.Name}", content), cancellationToken);
        }
    }

    /// <summary>The regular files of /out in name order, up to the limit; everything else is named with the reason.</summary>
    /// <remarks>
    /// The whole folder is read before choosing, which is safe because its tmpfs caps it; choosing by name keeps
    /// the answer the same for the same run, where archive order is whatever the directory listing was.
    /// </remarks>
    public static async Task<Collected> UnpackAsync(Stream archive, int maxFiles, CancellationToken cancellationToken)
    {
        await using var reader = new TarReader(archive, leaveOpen: true);
        List<OutputFile> found = [];
        List<string> omitted = [];

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            var name = BelowOut(entry.Name);

            if (name.Length == 0 || entry.EntryType is TarEntryType.Directory)
                continue;

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                omitted.Add($"{name}: a link or a special file, not a regular file");
                continue;
            }

            found.Add(new OutputFile(name, MediaTypeOf(name), await BytesAsync(entry, cancellationToken)));
        }

        var ordered = found.OrderBy(file => file.Name, StringComparer.Ordinal).ToList();

        return new Collected(
            [.. ordered.Take(maxFiles)],
            [.. omitted, .. ordered.Skip(maxFiles).Select(file => $"{file.Name}: past the limit of {maxFiles} files")]);
    }

    /// <summary>Whether the whole archive fitted under the cap; the copy stops at the first byte past it.</summary>
    public static async Task<bool> IsCopiedWithinAsync(Stream source, Stream target, long cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;

        for (var read = await source.ReadAsync(buffer, cancellationToken); read > 0; read = await source.ReadAsync(buffer, cancellationToken))
        {
            copied += read;
            if (copied > cap)
                return false;

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return true;
    }

    private static PaxTarEntry Folder(string name) => new(TarEntryType.Directory, $"{name}/")
    {
        Mode = Listable,
        Uid = 0,
        Gid = 0
    };

    private static PaxTarEntry File(string name, Stream content) => new(TarEntryType.RegularFile, name)
    {
        DataStream = content,
        Mode = Readable,
        Uid = 0,
        Gid = 0
    };

    /// <summary>The entry's path with the archived folder's own name taken off: out/charts/a.png becomes charts/a.png.</summary>
    private static string BelowOut(string entryName)
    {
        var slash = entryName.IndexOf('/', StringComparison.Ordinal);

        return slash < 0 ? string.Empty : entryName[(slash + 1)..].TrimEnd('/');
    }

    private static string MediaTypeOf(string name) =>
        MediaTypes.TryGetContentType(name, out var mediaType) ? mediaType : "application/octet-stream";

    private static async Task<byte[]> BytesAsync(TarEntry entry, CancellationToken cancellationToken)
    {
        if (entry.DataStream is null)
            return [];

        using var buffer = new MemoryStream();
        await entry.DataStream.CopyToAsync(buffer, cancellationToken);

        return buffer.ToArray();
    }
}
