using System.Formats.Tar;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Tests.Adapters;

namespace Snail.MCP.Sandbox.Tests.Crash;

/// <summary>Archives built to hurt the node: more bytes than declared room, more files than allowed, a stream cut short.</summary>
public sealed class ProjectArchiveCrashTests : IDisposable
{
    private static readonly Owner RunUser = new(65534, 65534);

    private readonly string _folder = TestFolder.Create();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Breaks the size: the limit is added up from the headers, so the archive is refused before its big file is copied.</summary>
    [Fact]
    public async Task RepackAsync_UnpacksPastTheLimit_IsRefusedAndCopiesNothingOfTheBigFile()
    {
        var source = await ProjectArchiveTests.ArchiveAsync(_folder, gzip: true, ProjectArchiveTests.File("small.py", "x"), ProjectArchiveTests.File("big.bin", new string('0', 64 * 1024)));
        var target = Path.Combine(_folder, "project.tar");

        var answer = await ProjectArchive.RepackAsync(source, target, RunUser, new ProjectLimits(32 * 1024, 100), CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("MB", answer.Error, StringComparison.Ordinal);
        Assert.Equal("tooLarge", answer.Type);
        Assert.True(new FileInfo(target).Length < 32 * 1024);
    }

    [Fact]
    public async Task RepackAsync_MoreFilesThanTheLimit_IsRefused()
    {
        TarEntry[] files = [.. Enumerable.Range(0, 11).Select(index => ProjectArchiveTests.File($"f{index}.py", "x"))];
        var source = await ProjectArchiveTests.ArchiveAsync(_folder, gzip: false, files);

        var answer = await ProjectArchive.RepackAsync(source, Path.Combine(_folder, "project.tar"), RunUser, new ProjectLimits(1024 * 1024, 10), CancellationToken.None);

        Assert.Contains("more than 10 files", answer.Error, StringComparison.Ordinal);
        Assert.Equal("tooLarge", answer.Type);
    }

    /// <summary>Breaks the stream: a gzip cut in the middle is an answer the model can act on, not a crash of the server.</summary>
    [Fact]
    public async Task RepackAsync_GzipCutShort_IsRefusedWithAReason()
    {
        var whole = await ProjectArchiveTests.ArchiveAsync(_folder, gzip: true, ProjectArchiveTests.File("app.py", new string('p', 50_000)));
        var bytes = await File.ReadAllBytesAsync(whole);
        var cut = Path.Combine(_folder, "cut.tar.gz");
        await File.WriteAllBytesAsync(cut, bytes[..(bytes.Length / 2)]);

        var answer = await ProjectArchive.RepackAsync(cut, Path.Combine(_folder, "project.tar"), RunUser, new ProjectLimits(1024 * 1024, 100), CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("could not be read", answer.Error, StringComparison.Ordinal);
        Assert.Equal("malformed", answer.Type);
    }

    [Fact]
    public async Task RepackAsync_EightRepacksOfOneArchiveAtOnce_EachGetsTheWholeProject()
    {
        var source = await ProjectArchiveTests.ArchiveAsync(_folder, gzip: true, ProjectArchiveTests.File("a.py", "a"), ProjectArchiveTests.File("src/b.py", "b"));

        var packed = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            ProjectArchive.RepackAsync(source, Path.Combine(_folder, $"p{index}.tar"), RunUser, new ProjectLimits(1024 * 1024, 100), CancellationToken.None))));

        Assert.All(packed, answer => Assert.Equal(["a.py", "src/b.py"], answer.Value!.Paths));
    }
}
