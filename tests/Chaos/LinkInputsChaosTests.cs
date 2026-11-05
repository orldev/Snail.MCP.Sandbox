using System.Text;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Configuration;
using Snail.MCP.Sandbox.Adapters;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>The names a run's inputs are written under, against keys the node's disk cannot take.</summary>
public sealed class LinkInputsChaosTests
{
    /// <summary>
    /// Breaks the file name: a key whose last segment is longer than a file name may be, 255 bytes on the node's disk,
    /// is cut to one, so the download is created rather than thrown at and the call still answers.
    /// </summary>
    [Fact]
    public void Names_SegmentLongerThanAFileName_FitsOne()
    {
        var name = LinkInputs.Names([new Uri($"http://minio:9000/b/{new string('a', 300)}.csv")]).Single();

        Assert.InRange(Encoding.UTF8.GetByteCount(name), 1, 255);
        Assert.EndsWith(".csv", name, StringComparison.Ordinal);
    }
}

/// <summary>The download against an object store that is not there.</summary>
public sealed class LinkInputsDownloadChaosTests
{
    /// <summary>
    /// Breaks the fetch: a link whose host refuses the connection — the object store down, a wrong port in the signed link —
    /// throws out of the HTTP client, and nothing between it and the job turns that into an answer. The job ends Failed with
    /// the exception's words, and the model is told the sandbox was shutting down.
    /// </summary>
    [Fact]
    public async Task Fetch_HostThatRefusesTheConnection_IsAnAnswerNotAnException()
    {
        using var refusing = new Refusing();
        var inputs = new LinkInputs(new Clients(refusing), Options.Create(new SandboxOptions { AllowedHosts = ["objects.example:9000"] }));
        var folder = TestFolder.Create();

        var answer = await inputs.FetchAsync([new Uri("http://objects.example:9000/b/sales.csv?X-Amz-Signature=1")], folder, CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("sales.csv", answer.Error, StringComparison.Ordinal);
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused (objects.example:9000)");
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
