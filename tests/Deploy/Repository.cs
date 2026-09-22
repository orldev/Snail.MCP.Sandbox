namespace Snail.MCP.Sandbox.Tests.Deploy;

/// <summary>The checkout the tests run from, found by its solution file, so the files they guard are read as committed.</summary>
internal static class Repository
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);

        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Snail.MCP.Sandbox.slnx")))
            folder = folder.Parent;

        return folder?.FullName ?? throw new InvalidOperationException("The tests run outside the repository they guard.");
    }
}
