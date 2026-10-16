using System.Formats.Tar;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Domain;
using Snail.MCP.Sandbox.Tests.Adapters;
using Snail.MCP.Sandbox.Tests.Application;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>A test run under the names a platform gives the project archive.</summary>
public sealed class ProjectArchiveChaosTests : IDisposable
{
    private readonly string _folder = TestFolder.Create();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// Breaks the name: an archive that arrives as project.tar — the name a key ending in project.tar lands under — used
    /// to be rewritten into project.tar of the same folder, the very file being read, and the call ended in an
    /// IOException instead of an answer.
    /// </summary>
    [Fact]
    public async Task RunAsync_ArchiveArrivedAsProjectTar_AnswersInsteadOfThrowing()
    {
        var options = Options.Create(new SandboxOptions { WorkRoot = _folder });
        var runs = new TestRuns(new Arrivals(), new TarProjectArchives(options), new TestRunsTests.Containers(), options);

        var answer = await runs.RunAsync("http://minio:9000/b/project.tar", null, null, CancellationToken.None);

        Assert.True(answer.IsSuccess);
        Assert.Equal(TestOutcome.Passed, answer.Value!.Outcome);
    }

    /// <summary>Inputs that write a real one-file project under the name the link ends in, as the downloads do.</summary>
    private sealed class Arrivals : IInputs
    {
        public async Task<Answer<IReadOnlyList<InputFile>>> FetchAsync(IReadOnlyList<Uri> links, string folder, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(folder);
            var arrived = Path.Combine(folder, links[0].Segments[^1]);

            await using (var file = File.Create(arrived))
            await using (var writer = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: true))
                await writer.WriteEntryAsync(ProjectArchiveTests.File("test_a.py", "def test_a(): pass"), cancellationToken);

            return Answer.Ok<IReadOnlyList<InputFile>>([new InputFile(links[0].Segments[^1], arrived, new FileInfo(arrived).Length)]);
        }

        public void Discard(string folder)
        {
        }
    }
}
