using System.Text.Json;
using BenchmarkDotNet.Attributes;
using ModelContextProtocol.Protocol;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Domain;
using Snail.MCP.Sandbox.Tools;
using Snail.Sdk.Mcp.Hosting.Jobs;

namespace Snail.MCP.Sandbox.Benchmarks;

/// <summary>What answering a run that left a file costs: told from the run itself, or read back from the job's report.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class AnswerBenchmarks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private Answer<RunOutcome> _answered = default!;
    private JobReport _ended = default!;

    [GlobalSetup]
    public void Setup()
    {
        var bytes = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        var answer = Answer.Ok(new RunOutcome(0, "done\n", string.Empty, TimeSpan.FromSeconds(1)) { Outputs = [new OutputFile("chart.png", "image/png", bytes)] });

        _answered = answer;
        _ended = new JobReport("job://sandbox/python/abc", JobState.Succeeded, Now, Now, 1, null, JsonSerializer.SerializeToElement(ToolResponse.Answered(answer), ToolResponse.RunResult));
    }

    [Benchmark(Baseline = true)]
    public CallToolResult ToldFromTheRun() => ToolResponse.Run(_ended, _answered);

    [Benchmark]
    public CallToolResult ReadBackFromTheReport() => ToolResponse.Run(_ended);
}
