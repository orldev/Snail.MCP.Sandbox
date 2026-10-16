using BenchmarkDotNet.Attributes;
using Snail.MCP.Sandbox.Adapters;

namespace Snail.MCP.Sandbox.Benchmarks;

/// <summary>What every run does with its standard output: keep its head and its tail within the limit as it streams in.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class OutputBenchmarks
{
    private readonly byte[] _chunk = new byte[8192];

    [GlobalSetup]
    public void Setup() => Array.Fill(_chunk, (byte)'x');

    [Benchmark]
    public int KeepAMegabyte()
    {
        var kept = new KeptOutput(65536);

        for (var index = 0; index < 128; index++)
            kept.Append(_chunk);

        return kept.Text.Length;
    }
}
