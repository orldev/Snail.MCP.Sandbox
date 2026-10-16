using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Snail.MCP.Sandbox.Benchmarks.ReportBenchmarks).Assembly).Run(args);
