using System.Text;
using Snail.MCP.Sandbox.Adapters;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>The names a run's inputs are written under, against keys the node's disk cannot take.</summary>
public sealed class LinkInputsChaosTests
{
    /// <summary>
    /// Breaks the file name: a key whose last segment is longer than a file name may be, 255 bytes on the node's disk,
    /// is cut to one, so the download is created rather than thrown at and the call still answers.
    /// </summary>
    [Fact]
    public void Names_SegmentLongerThanAFileName_FitsOne()
    {
        var name = LinkInputs.Names([new Uri($"http://minio:9000/b/{new string('a', 300)}.csv")]).Single();

        Assert.InRange(Encoding.UTF8.GetByteCount(name), 1, 255);
        Assert.EndsWith(".csv", name, StringComparison.Ordinal);
    }
}
