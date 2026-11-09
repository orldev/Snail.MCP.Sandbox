using System.Text;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>Downloads the inputs of a run from signed links, only from the hosts the deployment names.</summary>
/// <remarks>
/// The links come from the platform, which signs only files the task's own journal names. This side does not
/// trust that: it still refuses a host outside the list, follows no redirects and counts every byte against
/// the limits while reading, because the length a server announces is not the length it sends.
/// </remarks>
public sealed class LinkInputs(IHttpClientFactory clients, IOptions<SandboxOptions> options) : IInputs
{
    /// <summary>The most bytes a file name may take on the node's disk: ext4's and tmpfs's limit, in bytes of UTF-8, not characters.</summary>
    private const int LongestName = 255;

    private const string StoreUnreachable = "The object store did not answer the sandbox; this is the platform, not the code. Say so in the answer rather than calling again.";

    /// <inheritdoc />
    public async Task<Answer<IReadOnlyList<InputFile>>> FetchAsync(IReadOnlyList<Uri> links, string folder, CancellationToken cancellationToken)
    {
        var limits = options.Value;

        if (links.Count > limits.MaxInputFiles)
            return Answer.Failed(
                $"{links.Count} files were passed; one run may read {limits.MaxInputFiles}.",
                "Pass only the files this piece of code needs.",
                ToolFailures.Arguments);

        if (links.FirstOrDefault(link => !IsAllowed(link, limits.AllowedHosts)) is { } forbidden)
            return Answer.Failed(
                $"A file link points at {forbidden.Authority}, which this sandbox does not read from.",
                "Pass the keys of the task's own files; the platform turns them into links this sandbox accepts.",
                FailureTypes.NotAllowed);

        Directory.CreateDirectory(folder);
        List<InputFile> fetched = [];
        long total = 0;

        foreach (var (link, name) in links.Zip(Names(links)))
        {
            var path = Path.Combine(folder, name);
            var outcome = await DownloadAsync(link, path, limits.MaxInputBytes, limits.MaxInputTotalBytes - total, cancellationToken);

            if (outcome.Error is { } error)
                return Answer.Failed($"{name}: {error}", outcome.Hint, outcome.Type);

            total += outcome.Value;
            fetched.Add(new InputFile(name, path, outcome.Value));
        }

        return Answer.Ok<IReadOnlyList<InputFile>>(fetched);
    }

    /// <inheritdoc />
    public void Discard(string folder)
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>A link over http or https whose host, or host:port, is named in the list.</summary>
    internal static bool IsAllowed(Uri link, IReadOnlyCollection<string> hosts) =>
        link.IsAbsoluteUri
        && link.Scheme is "https" or "http"
        && hosts.Any(host => string.Equals(host, link.Host, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(host, link.Authority, StringComparison.OrdinalIgnoreCase));

    /// <summary>The file name each link is placed under: the last segment of its path, never a path, made distinct.</summary>
    internal static IReadOnlyList<string> Names(IReadOnlyList<Uri> links)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> names = [];

        foreach (var link in links)
        {
            var unescaped = Uri.UnescapeDataString(link.Segments.LastOrDefault() ?? string.Empty).Replace('\\', '/');
            var bare = Path.GetFileName(unescaped.TrimEnd('/'));
            var safe = string.Concat(bare.Select(character => char.IsControl(character) || character is ':' ? '_' : character));
            var candidate = Fitted(safe is "" or "." or ".." ? "input" : safe);
            var name = candidate;

            for (var copy = 2; !taken.Add(name); copy++)
                name = Fitted(candidate, $" ({copy})");

            names.Add(name);
        }

        return names;
    }

    /// <summary>The name within what a file name may take, its suffix and extension kept and its stem cut; the extension goes too when it alone is past the limit.</summary>
    private static string Fitted(string candidate, string suffix = "")
    {
        var extension = Path.GetExtension(candidate);
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var room = LongestName - Encoding.UTF8.GetByteCount(suffix) - Encoding.UTF8.GetByteCount(extension);

        return room > 0
            ? $"{Cut(stem, room)}{suffix}{extension}"
            : $"{Cut(candidate, LongestName - Encoding.UTF8.GetByteCount(suffix))}{suffix}";
    }

    /// <summary>The text's leading characters that fit in the bytes, whole characters only.</summary>
    private static string Cut(string text, int bytes)
    {
        var kept = new StringBuilder();
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            used += rune.Utf8SequenceLength;
            if (used > bytes)
                break;

            kept.Append(rune.ToString());
        }

        return kept.ToString();
    }

    /// <summary>The bytes written, or why the link could not be read: a refusal, an unreachable store, a download that broke off or outlasted the client's own patience.</summary>
    /// <remarks>
    /// A store that refuses the connection, a transfer reset halfway and a download past the client's two minutes all
    /// throw out of the client; thrown out of a job, they would end it failed with no answer, and the model would be told
    /// to start the run again against the same dead store.
    /// </remarks>
    private async Task<Answer<long>> DownloadAsync(Uri link, string path, long fileLimit, long totalLeft, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(link, path, fileLimit, totalLeft, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return Answer.Failed($"the link could not be read: {exception.Message}", StoreUnreachable, FailureTypes.Unreachable);
        }
        catch (IOException exception)
        {
            return Answer.Failed($"the download broke off: {exception.Message}", StoreUnreachable, FailureTypes.Unreachable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Answer.Failed("the download did not finish within the two minutes the sandbox gives a link.", StoreUnreachable, FailureTypes.Unreachable);
        }
    }

    private async Task<Answer<long>> ReadAsync(Uri link, string path, long fileLimit, long totalLeft, CancellationToken cancellationToken)
    {
        using var http = clients.CreateClient(nameof(LinkInputs));
        using var response = await http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return Answer.Failed(
                $"the link answered {(int)response.StatusCode}.",
                "A signed link lives for minutes; if the step ran long, call again and the platform signs a fresh one.",
                FailureTypes.LinkRefused);

        var limit = Math.Min(fileLimit, totalLeft);
        if (response.Content.Headers.ContentLength > limit)
            return Answer.Failed($"the file is {response.Content.Headers.ContentLength} bytes and the limit left is {limit} bytes.", type: FailureTypes.TooLarge);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(path);

        var buffer = new byte[81920];
        long written = 0;

        for (var read = await source.ReadAsync(buffer, cancellationToken); read > 0; read = await source.ReadAsync(buffer, cancellationToken))
        {
            written += read;
            if (written > limit)
                return Answer.Failed($"the file is larger than the limit left, {limit} bytes.", type: FailureTypes.TooLarge);

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Answer.Ok(written);
    }
}
