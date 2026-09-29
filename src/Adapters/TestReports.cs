using System.Xml;
using System.Xml.Linq;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>The counts and failures in the reports a test run wrote: TRX from dotnet, JUnit XML from pytest.</summary>
/// <remarks>
/// Each test is counted from its own result element rather than from a summary, because a summary is one number the
/// project's own code could have written, while a failure has to come with a name. The reports are written by code
/// the sandbox does not trust, so a document type definition is refused outright and a report that does not parse is
/// passed over rather than failing the run.
/// </remarks>
internal static class TestReports
{
    /// <summary>Most failures named in one answer; the count still says how many there were.</summary>
    public const int MaxFailures = 20;

    /// <summary>Most characters of one failure's message kept, from its start.</summary>
    public const int MaxMessageLength = 1000;

    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private static readonly XmlReaderSettings Untrusted = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    /// <summary>The counts of every report among the files, added up; null when none of them is a report.</summary>
    public static TestCounts? Read(IReadOnlyList<OutputFile> files)
    {
        List<TestCounts> read = [.. files.Select(Parsed).OfType<TestCounts>()];

        if (read.Count == 0)
            return null;

        return new TestCounts(
            read.Sum(counts => counts.Total),
            read.Sum(counts => counts.Passed),
            read.Sum(counts => counts.Failed),
            read.Sum(counts => counts.Skipped),
            [.. read.SelectMany(counts => counts.Failures).Take(MaxFailures)]);
    }

    private static TestCounts? Parsed(OutputFile file)
    {
        if (Document(file.Bytes) is not { Root: { } root })
            return null;

        if (root.Name == Trx + "TestRun")
            return FromTrx(root);

        return root.Name.LocalName is "testsuites" or "testsuite" ? FromJUnit(root) : null;
    }

    private static XDocument? Document(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, Untrusted);

            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static TestCounts FromTrx(XElement root)
    {
        List<XElement> results = [.. root.Descendants(Trx + "UnitTestResult")];
        List<XElement> failed = [.. results.Where(result => (string?)result.Attribute("outcome") is "Failed" or "Error" or "Timeout" or "Aborted")];
        var passed = results.Count(result => (string?)result.Attribute("outcome") is "Passed");

        return new TestCounts(results.Count, passed, failed.Count, results.Count - passed - failed.Count, [.. failed.Select(TrxFailure)]);
    }

    private static TestFailure TrxFailure(XElement result) =>
        new((string?)result.Attribute("testName") ?? "?", Cut((string?)result.Descendants(Trx + "Message").FirstOrDefault() ?? string.Empty));

    private static TestCounts FromJUnit(XElement root)
    {
        List<XElement> cases = [.. root.DescendantsAndSelf("testcase")];
        List<XElement> failed = [.. cases.Where(testCase => testCase.Element("failure") is not null || testCase.Element("error") is not null)];
        var skipped = cases.Count(testCase => testCase.Element("skipped") is not null && !failed.Contains(testCase));

        return new TestCounts(cases.Count, cases.Count - failed.Count - skipped, failed.Count, skipped, [.. failed.Select(JUnitFailure)]);
    }

    private static TestFailure JUnitFailure(XElement testCase)
    {
        var problem = testCase.Element("failure") ?? testCase.Element("error")!;
        var name = (string?)testCase.Attribute("classname") is { Length: > 0 } owner
            ? $"{owner}.{(string?)testCase.Attribute("name")}"
            : (string?)testCase.Attribute("name") ?? "?";

        return new TestFailure(name, Cut((string?)problem.Attribute("message") is { Length: > 0 } message ? message : problem.Value));
    }

    private static string Cut(string message)
    {
        var trimmed = message.Trim();

        return trimmed.Length <= MaxMessageLength ? trimmed : $"{trimmed[..MaxMessageLength]}…";
    }
}
