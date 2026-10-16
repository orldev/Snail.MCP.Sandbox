using System.Text;
using BenchmarkDotNet.Attributes;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Benchmarks;

/// <summary>What every test run does with what it wrote: count the results of its TRX and JUnit reports.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ReportBenchmarks
{
    private IReadOnlyList<OutputFile> _reports = [];

    [GlobalSetup]
    public void Setup() => _reports =
    [
        new OutputFile("tests.trx", "application/xml", Encoding.UTF8.GetBytes(Trx(500))),
        new OutputFile("junit.xml", "application/xml", Encoding.UTF8.GetBytes(JUnit(500)))
    ];

    [Benchmark]
    public int Read() => TestReports.Read(_reports)?.Total ?? 0;

    private static string Trx(int count)
    {
        var results = new StringBuilder();

        for (var index = 0; index < count; index++)
        {
            var outcome = index % 25 == 0 ? "Failed" : index % 40 == 0 ? "NotExecuted" : "Passed";
            var message = outcome == "Failed" ? $"<Output><ErrorInfo><Message>Assert.Equal() Failure: expected {index}, got {index + 1}</Message></ErrorInfo></Output>" : string.Empty;

            results.Append($"<UnitTestResult testName=\"Suite.Test{index}\" outcome=\"{outcome}\">{message}</UnitTestResult>");
        }

        return $"<?xml version=\"1.0\"?><TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>{results}</Results></TestRun>";
    }

    private static string JUnit(int count)
    {
        var cases = new StringBuilder();

        for (var index = 0; index < count; index++)
        {
            var body = index % 25 == 0 ? $"<failure message=\"assert {index} == {index + 1}\">Traceback (most recent call last)</failure>"
                : index % 40 == 0 ? "<skipped/>"
                : string.Empty;

            cases.Append($"<testcase classname=\"tests.test_suite\" name=\"test_{index}\">{body}</testcase>");
        }

        return $"<?xml version=\"1.0\"?><testsuites><testsuite name=\"pytest\">{cases}</testsuite></testsuites>";
    }
}
