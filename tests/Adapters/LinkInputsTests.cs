using System.Net;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Adapters;

public sealed class LinkInputsTests : IDisposable
{
    private readonly string _folder = Path.Combine(TestFolder.Create(), "inputs");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task FetchAsync_LinkToAnAllowedHost_PlacesTheFileUnderItsName()
    {
        var inputs = Inputs(_ => Body("month,total\n"));

        var answer = await inputs.FetchAsync([new Uri("http://minio:9000/task-journal/u/attachments/sales.csv?X-Amz-Signature=abc")], _folder, CancellationToken.None);

        var file = Assert.Single(answer.Value!);
        Assert.Equal("sales.csv", file.Name);
        Assert.Equal("month,total\n", await File.ReadAllTextAsync(file.Path));
    }

    /// <summary>An open list would turn the sandbox into a fetcher of anything the node reaches — the cluster first.</summary>
    [Fact]
    public async Task FetchAsync_LinkToAnotherHost_IsRefusedWithoutAnyRequest()
    {
        var asked = 0;
        var inputs = Inputs(_ =>
        {
            asked++;
            return Body("secret");
        });

        var answer = await inputs.FetchAsync([new Uri("http://vault.service:8200/v1/secret")], _folder, CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("vault.service:8200", answer.Error, StringComparison.Ordinal);
        Assert.Equal(0, asked);
    }

    /// <summary>The announced length is not the length a server sends; bytes are counted while reading.</summary>
    [Fact]
    public async Task FetchAsync_BodyLargerThanTheLimitWithoutALength_StopsWhileReading()
    {
        var inputs = Inputs(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Unannounced(new byte[2048])) }, maxBytes: 1024);

        var answer = await inputs.FetchAsync([new Uri("http://minio:9000/b/big.bin")], _folder, CancellationToken.None);

        Assert.False(answer.IsSuccess);
        Assert.Contains("larger than the limit", answer.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_ExpiredLink_SaysAFreshOneIsSignedOnTheNextCall()
    {
        var inputs = Inputs(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var answer = await inputs.FetchAsync([new Uri("http://minio:9000/b/sales.csv")], _folder, CancellationToken.None);

        Assert.Contains("403", answer.Error, StringComparison.Ordinal);
        Assert.Contains("fresh one", answer.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_MoreFilesThanAllowed_IsRefused() =>
        Assert.False((await Inputs(_ => Body("x"), maxFiles: 1)
            .FetchAsync([new Uri("http://minio:9000/b/a.csv"), new Uri("http://minio:9000/b/b.csv")], _folder, CancellationToken.None)).IsSuccess);

    /// <summary>A crafted link must not climb out of the run's folder, and two files of one name must not overwrite each other.</summary>
    [Fact]
    public void Names_LinksWithEscapedPathsAndRepeats_AreBareAndDistinct() =>
        Assert.Equal(
            ["passwd", "report.pdf", "report (2).pdf"],
            LinkInputs.Names([
                new Uri("http://minio:9000/b/..%2F..%2Fetc%2Fpasswd"),
                new Uri("http://minio:9000/b/u1/report.pdf"),
                new Uri("http://minio:9000/b/u2/report.pdf")
            ]));

    [Theory]
    [InlineData("http://minio:9000/b/a.csv", true)]
    [InlineData("https://minio/b/a.csv", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://minio:9000/b/a.csv", false)]
    [InlineData("http://minio.evil:9000/b/a.csv", false)]
    public void IsAllowed_Link_OnlyForHttpToANamedHost(string link, bool expected) =>
        Assert.Equal(expected, LinkInputs.IsAllowed(new Uri(link), ["minio", "minio:9000"]));

    private static HttpResponseMessage Body(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static LinkInputs Inputs(Func<HttpRequestMessage, HttpResponseMessage> answer, long maxBytes = 1024 * 1024, int maxFiles = 20) =>
        new(new Clients(new Handler(answer)),
            Options.Create(new SandboxOptions { AllowedHosts = ["minio:9000"], MaxInputBytes = maxBytes, MaxInputFiles = maxFiles }));

    /// <summary>A body whose length is not known in advance, the way a streamed response arrives.</summary>
    private sealed class Unannounced(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>Hands out clients over one scripted handler, which outlives each of them as a factory's pooled handler does.</summary>
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
