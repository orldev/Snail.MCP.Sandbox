using System.Text;
using Snail.MCP.Sandbox.Adapters;

namespace Snail.MCP.Sandbox.Tests.Adapters;

public sealed class KeptOutputTests
{
    [Fact]
    public void Text_WithinTheLimit_IsEverythingInOrder()
    {
        var output = Written(limit: 10, chunk: 3, "abcdefgh");

        Assert.Equal("abcdefgh", output.Text);
        Assert.False(output.IsCut);
    }

    [Fact]
    public void Text_ExactlyAtTheLimit_IsNotCut() =>
        Assert.False(Written(limit: 8, chunk: 8, "abcdefgh").IsCut);

    /// <summary>A traceback is read from the bottom: keeping only the head would drop the line that says what failed.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(16)]
    public void Text_PastTheLimit_KeepsTheBeginningAndTheEndAndCountsTheCut(int chunk)
    {
        var output = Written(limit: 8, chunk, "0123456789ABCDEF");

        Assert.Equal("0123\n[... 8 bytes cut ...]\nCDEF", output.Text);
        Assert.True(output.IsCut);
    }

    private static KeptOutput Written(int limit, int chunk, string text)
    {
        var output = new KeptOutput(limit);

        foreach (var piece in Encoding.UTF8.GetBytes(text).Chunk(chunk))
            output.Append(piece);

        return output;
    }
}
