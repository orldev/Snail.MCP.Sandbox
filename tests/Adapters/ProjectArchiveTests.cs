using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Snail.MCP.Sandbox.Adapters;

namespace Snail.MCP.Sandbox.Tests.Adapters;

/// <summary>
/// The archive a project arrives in, rewritten for the container: everything the run user's, every folder a file sits
/// in made explicitly, links left out, and nothing that climbs out of the project.
/// </summary>
public sealed class ProjectArchiveTests : IDisposable
{
    private static readonly Owner RunUser = new(65534, 65534);

    private static readonly ProjectLimits Roomy = new(1024 * 1024, 100);

    private readonly string _folder = Directory.CreateTempSubdirectory("sandbox-project-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task RepackAsync_GzippedProject_OwnsEveryEntryToTheRunUserUnderTheProjectFolder()
    {
        var source = await ArchiveAsync(gzip: true, File("src/cache.py", "cache = {}"), File("test_cache.py", "def test(): pass"));

        var packed = (await ProjectArchive.RepackAsync(source, Target(), RunUser, Roomy, CancellationToken.None)).Value!;

        var entries = await EntriesAsync(packed.Archive);
        Assert.Equal(["src/cache.py", "test_cache.py"], packed.Paths);
        Assert.All(entries, entry => Assert.Equal(65534, entry.Uid));
        Assert.All(entries, entry => Assert.Equal(65534, entry.Gid));
        Assert.Contains(entries, entry => entry.Name == "project/src/cache.py");
    }

    /// <summary>A folder only implied by a file's path would be made by the daemon as root's, and the build could not write in it.</summary>
    [Fact]
    public async Task RepackAsync_FoldersOnlyImpliedByPaths_AreWrittenAsFoldersOfTheirOwn()
    {
        var source = await ArchiveAsync(gzip: false, File("src/Shop/Api/Program.cs", "return;"));

        var packed = (await ProjectArchive.RepackAsync(source, Target(), RunUser, Roomy, CancellationToken.None)).Value!;

        var folders = (await EntriesAsync(packed.Archive)).Where(entry => entry.EntryType is TarEntryType.Directory).Select(entry => entry.Name.TrimEnd('/'));
        Assert.Equal(["project", "deps", "project/src", "project/src/Shop", "project/src/Shop/Api"], folders);
    }

    [Fact]
    public async Task RepackAsync_ExecutableScript_StaysExecutable()
    {
        var source = await ArchiveAsync(gzip: false, File("build.sh", "echo", UnixFileMode.UserRead | UnixFileMode.UserExecute));

        var packed = (await ProjectArchive.RepackAsync(source, Target(), RunUser, Roomy, CancellationToken.None)).Value!;

        var script = (await EntriesAsync(packed.Archive)).Single(entry => entry.Name == "project/build.sh");
        Assert.True((script.Mode & UnixFileMode.OtherExecute) != 0);
    }

    [Fact]
    public async Task RepackAsync_LeadingDotSlash_IsTheSameProject()
    {
        var source = await ArchiveAsync(gzip: false, File("./app.py", "print(1)"));

        var packed = (await ProjectArchive.RepackAsync(source, Target(), RunUser, Roomy, CancellationToken.None)).Value!;

        Assert.Equal(["app.py"], packed.Paths);
    }

    /// <summary>A link could point anywhere in the container once it is there; it is named and left out.</summary>
    [Fact]
    public async Task RepackAsync_SymbolicLink_IsLeftOutAndNamed()
    {
        var source = await ArchiveAsync(gzip: false, File("app.py", "print(1)"), new PaxTarEntry(TarEntryType.SymbolicLink, "passwd") { LinkName = "/etc/passwd" });

        var packed = (await ProjectArchive.RepackAsync(source, Target(), RunUser, Roomy, CancellationToken.None)).Value!;

        Assert.Equal(["app.py"], packed.Paths);
        Assert.Contains("passwd", Assert.Single(packed.Omitted), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.py")]
    [InlineData("src/../../escape.py")]
    [InlineData("/etc/cron.d/job")]
    public async Task RepackAsync_PathClimbingOutOfTheProject_RefusesTheWholeProject(string path)
    {
        var source = await ArchiveAsync(gzip: false, File("app.py", "print(1)"), File(path, "x"));

        var answer = await ProjectArchive.RepackAsync(source, Target(), RunUser, Roomy, CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("outside the project", answer.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepackAsync_NotAnArchive_IsRefusedWithAHint()
    {
        var source = Path.Combine(_folder, "notes.txt");
        await System.IO.File.WriteAllTextAsync(source, new string('x', 2048));

        var answer = await ProjectArchive.RepackAsync(source, Target(), RunUser, Roomy, CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("tar", answer.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/app.py", "src/app.py")]
    [InlineData("./src/app.py", "src/app.py")]
    [InlineData("src/", "src")]
    [InlineData("..", null)]
    [InlineData("a/./b", null)]
    [InlineData("a//b", null)]
    [InlineData("C:\\\\x", null)]
    [InlineData(".", null)]
    public void Inside_EntryName_IsThePathBelowTheRootOrNothing(string entryName, string? expected) =>
        Assert.Equal(expected, ProjectArchive.Inside(entryName));

    private string Target() => Path.Combine(_folder, $"{Guid.NewGuid():N}.tar");

    internal static PaxTarEntry File(string name, string text, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)), Mode = mode };

    private Task<string> ArchiveAsync(bool gzip, params TarEntry[] entries) => ArchiveAsync(_folder, gzip, entries);

    internal static async Task<string> ArchiveAsync(string folder, bool gzip, params TarEntry[] entries)
    {
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}.tar{(gzip ? ".gz" : string.Empty)}");

        await using (var file = System.IO.File.Create(path))
        await using (Stream target = gzip ? new GZipStream(file, CompressionLevel.Fastest) : file)
        await using (var writer = new TarWriter(target, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
                await writer.WriteEntryAsync(entry);
        }

        return path;
    }

    private static async Task<List<TarEntry>> EntriesAsync(string archive)
    {
        await using var file = System.IO.File.OpenRead(archive);
        await using var reader = new TarReader(file);
        List<TarEntry> entries = [];

        while (await reader.GetNextEntryAsync(copyData: true) is { } entry)
            entries.Add(entry);

        return entries;
    }
}
