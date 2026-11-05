using System.Text.Json;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Domain;
using Snail.MCP.Sandbox.Tools;
using Snail.Sdk.Mcp.Hosting.Jobs;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>What an ended job costs the server to tell.</summary>
public sealed class AnswerAllocationChaosTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Breaks the budget: before a run was a job, a 32 MB file went to its block once. Now the file travels as base64 in the
    /// job's result, is read back as a string, decoded to bytes and encoded again for the block — three more copies of a file
    /// that was already base64. Measured here on the answering thread, which is what the server pays per answer on top of the
    /// result it keeps.
    /// </summary>
    [Fact]
    public void Run_EndedWithTheLargestFile_AllocatesNoMoreThanTwiceTheFileToAnswer()
    {
        var bytes = new byte[32 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        var answer = Answer.Ok(new RunOutcome(0, string.Empty, string.Empty, TimeSpan.FromSeconds(1)) { Outputs = [new OutputFile("big.bin", "application/octet-stream", bytes)] });
        var ended = new JobReport("job://sandbox/python/abc", JobState.Succeeded, Now, Now, 1, null, JsonSerializer.SerializeToElement(ToolResponse.Answered(answer), ToolResponse.RunResult));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = ToolResponse.Run(ended, answer);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2, result.Content.Count);
        Assert.True(allocated <= 2L * bytes.Length, $"answering a {bytes.Length / 1024 / 1024} MB file allocated {allocated / 1024 / 1024} MB");
    }
}
