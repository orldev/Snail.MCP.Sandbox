using System.Text;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Domain;

namespace Snail.MCP.Sandbox.Tests.Adapters;

/// <summary>The counts a test run answers with, read test by test from the reports dotnet and pytest write.</summary>
public sealed class TestReportsTests
{
    [Fact]
    public void Read_TrxWithAFailure_CountsEachResultAndNamesTheFailure()
    {
        var counts = TestReports.Read([Report("results/a.trx", Trx(Result("Adds", "Passed"), Result("Divides", "Failed", "Expected 2, got 3"), Result("Later", "NotExecuted")))])!;

        Assert.Equal(3, counts.Total);
        Assert.Equal(1, counts.Passed);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(1, counts.Skipped);
        Assert.Equal(new TestFailure("Divides", "Expected 2, got 3"), Assert.Single(counts.Failures));
    }

    [Fact]
    public void Read_JUnitFromPytest_NamesTheFailureByClassAndTest()
    {
        var junit = """
            <testsuites><testsuite name="pytest" tests="3">
              <testcase classname="tests.test_cache" name="test_hit" />
              <testcase classname="tests.test_cache" name="test_clear"><failure message="assert 1 == 0">trace</failure></testcase>
              <testcase classname="tests.test_cache" name="test_slow"><skipped message="slow" /></testcase>
            </testsuite></testsuites>
            """;

        var counts = TestReports.Read([Report("results/junit.xml", junit)])!;

        Assert.Equal(3, counts.Total);
        Assert.Equal(1, counts.Passed);
        Assert.Equal(1, counts.Skipped);
        Assert.Equal(new TestFailure("tests.test_cache.test_clear", "assert 1 == 0"), Assert.Single(counts.Failures));
    }

    /// <summary>A solution with two test projects writes one report each; the answer is about the solution.</summary>
    [Fact]
    public void Read_TwoReports_AddsThemUp()
    {
        var counts = TestReports.Read([Report("results/a.trx", Trx(Result("A", "Passed"))), Report("results/b.trx", Trx(Result("B", "Failed", "no")))])!;

        Assert.Equal(2, counts.Total);
        Assert.Equal(1, counts.Failed);
    }

    [Fact]
    public void Read_NoReportAmongTheFiles_IsNull() =>
        Assert.Null(TestReports.Read([Report("results/notes.txt", "hello"), Report("results/data.xml", "<root />")]));

    /// <summary>The report is written by the project's code: an entity expansion in it is refused, not expanded.</summary>
    [Fact]
    public void Read_ReportWithADocumentTypeDefinition_IsPassedOver()
    {
        var bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">]>
            <testsuite><testcase name="&lol2;" /></testsuite>
            """;

        Assert.Null(TestReports.Read([Report("results/junit.xml", bomb)]));
    }

    [Fact]
    public void Read_BrokenXml_IsPassedOverAndTheOtherReportsStillCount()
    {
        var counts = TestReports.Read([Report("results/broken.trx", "<TestRun"), Report("results/a.trx", Trx(Result("A", "Passed")))])!;

        Assert.Equal(1, counts.Total);
    }

    /// <summary>A run with hundreds of failures answers with the count and the first few; the rest would bury them.</summary>
    [Fact]
    public void Read_MoreFailuresThanTheAnswerHolds_NamesTheFirstAndCountsAll()
    {
        var results = Enumerable.Range(0, TestReports.MaxFailures + 5).Select(index => Result($"T{index}", "Failed", new string('x', TestReports.MaxMessageLength + 50)));

        var counts = TestReports.Read([Report("results/a.trx", Trx([.. results]))])!;

        Assert.Equal(TestReports.MaxFailures + 5, counts.Failed);
        Assert.Equal(TestReports.MaxFailures, counts.Failures.Count);
        Assert.Equal(TestReports.MaxMessageLength + 1, counts.Failures[0].Message.Length);
    }

    private static OutputFile Report(string name, string text) => new(name, "application/xml", Encoding.UTF8.GetBytes(text));

    private static string Result(string name, string outcome, string? message = null) =>
        message is null
            ? $"""<UnitTestResult testName="{name}" outcome="{outcome}" />"""
            : $"""<UnitTestResult testName="{name}" outcome="{outcome}"><Output><ErrorInfo><Message>{message}</Message></ErrorInfo></Output></UnitTestResult>""";

    private static string Trx(params string[] results) =>
        $"""<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{string.Concat(results)}</Results></TestRun>""";
}
