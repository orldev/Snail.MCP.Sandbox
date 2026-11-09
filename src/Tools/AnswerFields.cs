using System.Text.Json.Nodes;

namespace Snail.MCP.Sandbox.Tools;

/// <summary>The data of every answer, mapped from the records the run, the test run and the diagnosis end with.</summary>
public static class AnswerFields
{
    /// <summary>The node's readiness as the diagnosis answers it, a toolchain named as sandbox_run_tests takes it.</summary>
    public static JsonObject Of(Readiness readiness) => new()
    {
        ["dockerVersion"] = readiness.DockerVersion,
        ["runtime"] = readiness.Runtime,
        ["image"] = readiness.Image,
        ["untestable"] = new JsonArray([.. readiness.Untestable.Select(toolchain => (JsonNode)ToolAnswer.Word(toolchain, WordCase.Camel))])
    };

    /// <summary>What a run did, its files named and sized, and carried whole as base64 when <paramref name="withContent"/>.</summary>
    /// <param name="outcome">The run as the container ended it.</param>
    /// <param name="withContent">
    /// True for the job's result, which a late reader has nothing but; false for the text the model reads, whose files
    /// travel as blocks of their own.
    /// </param>
    public static RunSummary Of(RunOutcome outcome, bool withContent) => new(
        outcome.ExitCode,
        outcome.HasTimedOut,
        Math.Round(outcome.Duration.TotalSeconds, 2),
        outcome.Stdout,
        outcome.Stderr,
        outcome.IsStdoutCut,
        outcome.IsStderrCut,
        [.. outcome.Outputs.Select(file => new OutFile(file.Name, file.MediaType, file.Bytes.Length, withContent ? Convert.ToBase64String(file.Bytes) : null))],
        outcome.Omitted,
        Note(outcome));

    /// <summary>What a test run found out, its outcome and toolchain spelled as sandbox_run_tests takes them.</summary>
    public static TestSummary Of(TestReport report) => new(
        report.Toolchain is { } toolchain ? ToolAnswer.Word(toolchain, WordCase.Camel) : null,
        ToolAnswer.Word(report.Outcome, WordCase.Camel),
        report.Counts?.Total,
        report.Counts?.Passed,
        report.Counts?.Failed,
        report.Counts?.Skipped,
        [.. (report.Counts?.Failures ?? []).Select(failure => new FailedTest(failure.Name, failure.Message))],
        Math.Round(report.Duration.TotalSeconds, 2),
        report.Output,
        report.IsOutputCut,
        report.Omitted,
        report.Contacted,
        Note(report));

    private static string? Note(TestReport report) => report.Outcome switch
    {
        TestOutcome.Failed => Messages.TestsFailed,
        TestOutcome.BuildFailed => Messages.BuildFailed,
        TestOutcome.RestoreFailed => Messages.RestoreFailed,
        TestOutcome.TimedOut => Messages.TestsTimedOut,
        TestOutcome.NoTests => Messages.NoTests,
        TestOutcome.NothingToBuild => Messages.NothingToBuild,
        _ when report.Omitted.Count > 0 => Messages.ProjectOmitted,
        _ => null
    };

    private static string? Note(RunOutcome outcome) => outcome switch
    {
        { HasTimedOut: true } => Messages.TimedOut,
        { ExitCode: 137 } => Messages.Killed,
        { ExitCode: not 0 } => Messages.Failed,
        { Omitted.Count: > 0 } => Messages.Omitted,
        _ => null
    };
}
