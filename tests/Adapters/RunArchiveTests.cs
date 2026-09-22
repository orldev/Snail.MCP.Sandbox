using System.Formats.Tar;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Adapters;

public sealed class RunArchiveTests : IDisposable
{
    private const UnixFileMode Writable = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private readonly string _folder = Directory.CreateTempSubdirectory("sandbox-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Owner and mode are all that keep the inputs unwritable inside a volume the archive API needs writable.</summary>
    [Fact]
    public async Task PackAsync_CodeAndInputs_ArriveRootOwnedAndReadOnly()
    {
        var entries = await PackedAsync();

        Assert.All(entries, entry =>
        {
            Assert.Equal(0, entry.Uid);
            Assert.Equal(0, entry.Gid);
            Assert.Equal(default, entry.Mode & Writable);
        });
    }

    [Fact]
    public async Task PackAsync_CodeAndInputs_LandWhereTheExecutionLooksForThem()
    {
        var names = (await PackedAsync()).Select(entry => $"{Confinement.Root}/{entry.Name}").ToList();
        var execution = Confinement.Execution(new SandboxOptions());

        Assert.Contains(execution.Cmd[^1], names);
        Assert.Contains($"{execution.WorkingDir}/sales.csv", names);
    }

    [Fact]
    public async Task UnpackAsync_MoreFilesThanTheLimit_ReturnsTheFirstByNameAndNamesTheRest()
    {
        var collected = await RunArchive.UnpackAsync(Out(("c.txt", TarEntryType.RegularFile), ("a.txt", TarEntryType.RegularFile), ("b.txt", TarEntryType.RegularFile)), 2, CancellationToken.None);

        Assert.Equal(["a.txt", "b.txt"], collected.Files.Select(file => file.Name));
        Assert.Contains("c.txt: past the limit of 2 files", collected.Omitted);
    }

    [Fact]
    public async Task UnpackAsync_LinkInOut_IsNamedAndNotFollowed()
    {
        var collected = await RunArchive.UnpackAsync(Out(("passwd", TarEntryType.SymbolicLink)), 10, CancellationToken.None);

        Assert.Empty(collected.Files);
        Assert.StartsWith("passwd:", Assert.Single(collected.Omitted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpackAsync_FileInASubfolder_KeepsItsPathBelowOut()
    {
        var collected = await RunArchive.UnpackAsync(Out(("charts/", TarEntryType.Directory), ("charts/sales.svg", TarEntryType.RegularFile)), 10, CancellationToken.None);

        Assert.Equal("charts/sales.svg", Assert.Single(collected.Files).Name);
    }

    /// <summary>The archive is held in memory before reading, so what one run can make the server hold stops at the cap.</summary>
    [Fact]
    public async Task IsCopiedWithinAsync_ArchivePastTheCap_StopsAndSaysSo()
    {
        using var copied = new MemoryStream();

        var isCopied = await RunArchive.IsCopiedWithinAsync(new MemoryStream(new byte[4096]), copied, 1024, CancellationToken.None);

        Assert.False(isCopied);
        Assert.True(copied.Length <= 1024);
    }

    [Theory]
    [InlineData("report.csv", "text/csv")]
    [InlineData("chart.png", "image/png")]
    [InlineData("frame.parquet-part", "application/octet-stream")]
    public async Task UnpackAsync_FileName_CarriesTheMediaTypeOfItsExtension(string name, string mediaType)
    {
        var collected = await RunArchive.UnpackAsync(Out((name, TarEntryType.RegularFile)), 10, CancellationToken.None);

        Assert.Equal(mediaType, Assert.Single(collected.Files).MediaType);
    }

    private async Task<IReadOnlyList<TarEntry>> PackedAsync()
    {
        var sales = Path.Combine(_folder, "sales.csv");
        await File.WriteAllTextAsync(sales, "month,total\n");

        using var archive = new MemoryStream();
        await RunArchive.PackAsync(archive, "print(1)", [new InputFile("sales.csv", sales, 12)], CancellationToken.None);
        archive.Position = 0;

        using var reader = new TarReader(archive);
        List<TarEntry> entries = [];
        while (reader.GetNextEntry(copyData: true) is { } entry)
            entries.Add(entry);

        return entries;
    }

    private static MemoryStream Out(params (string Name, TarEntryType Type)[] entries)
    {
        var archive = new MemoryStream();

        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "out/"));
            foreach (var (name, type) in entries)
            {
                var entry = new PaxTarEntry(type, $"out/{name}");
                if (type is TarEntryType.SymbolicLink)
                    entry.LinkName = "/etc/passwd";
                if (type is TarEntryType.RegularFile)
                    entry.DataStream = new MemoryStream([1, 2, 3]);

                writer.WriteEntry(entry);
            }
        }

        archive.Position = 0;

        return archive;
    }
}
