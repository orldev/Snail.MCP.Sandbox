using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Tools;

namespace Snail.MCP.Sandbox.Benchmarks;

/// <summary>What find_tool does on every call: score each tool against the task and build the answer.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FindToolBenchmarks
{
    private readonly ToolIndex _index = new(new SessionGrant(new HttpContextAccessor()));

    [Benchmark]
    public string Matched() => _index.Answer("run python on a csv file and plot it", 5).ToJsonString();
}
