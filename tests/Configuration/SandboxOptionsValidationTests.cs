using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Configuration;

public sealed class SandboxOptionsValidationTests
{
    private static readonly SandboxOptionsValidation Validation = new();

    [Fact]
    public void Validate_Defaults_Succeed() =>
        Assert.True(Validation.Validate(null, new SandboxOptions()).Succeeded);

    /// <summary>An empty runtime would let the daemon pick its default and silently drop gVisor.</summary>
    [Fact]
    public void Validate_EmptyRuntime_Fails() =>
        Assert.True(Validation.Validate(null, new SandboxOptions { Runtime = " " }).Failed);

    [Theory]
    [InlineData("0:0")]
    [InlineData("000:1000")]
    public void Validate_RootUser_Fails(string user) =>
        Assert.True(Validation.Validate(null, new SandboxOptions { User = user }).Failed);

    /// <summary>/out is a tmpfs mounted for the run user, and a mount takes a uid, not a name.</summary>
    [Theory]
    [InlineData("root")]
    [InlineData("nobody")]
    [InlineData("1000")]
    [InlineData("")]
    public void Validate_UserWithoutNumericUidAndGid_Fails(string user) =>
        Assert.True(Validation.Validate(null, new SandboxOptions { User = user }).Failed);

    /// <summary>An address nobody listens on fails every restore; a proxy nobody is told of watches nothing.</summary>
    [Theory]
    [InlineData("http://mcp-sandbox:3128", 0)]
    [InlineData(null, 3128)]
    [InlineData("https://mcp-sandbox:3128", 3128)]
    [InlineData("http://run:x@mcp-sandbox:3128", 3128)]
    [InlineData("mcp-sandbox:3128", 3128)]
    [InlineData("http://mcp-sandbox:3128", 70000)]
    public void Validate_RestoreProxyHalfSetOrMisnamed_Fails(string? proxy, int port) =>
        Assert.True(Validation.Validate(null, new SandboxOptions { RestoreProxy = proxy, RestoreProxyPort = port }).Failed);

    [Fact]
    public void Validate_RestoreProxyWithItsPort_Succeeds() =>
        Assert.True(Validation.Validate(null, new SandboxOptions { RestoreProxy = "http://mcp-sandbox:3128", RestoreProxyPort = 3128 }).Succeeded);

    [Fact]
    public void Validate_RelativeDockerEndpoint_Fails() =>
        Assert.True(Validation.Validate(null, new SandboxOptions { DockerEndpoint = "docker.sock" }).Failed);

    /// <summary>An unset limit is no limit: Docker reads zero memory as unlimited.</summary>
    [Fact]
    public void Validate_ZeroMemory_Fails() =>
        Assert.True(Validation.Validate(null, new SandboxOptions { MemoryBytes = 0 }).Failed);

    [Fact]
    public void Validate_DefaultTimeoutAboveTheMaximum_Fails() =>
        Assert.True(Validation.Validate(null, new SandboxOptions { DefaultTimeoutSeconds = 400, MaxTimeoutSeconds = 300 }).Failed);

    /// <summary>The host's network, or another container's, would put the restore — which runs project code — beside the node's own services.</summary>
    [Theory]
    [InlineData("host")]
    [InlineData(" HOST ")]
    [InlineData("container:vault")]
    [InlineData("")]
    public void Validate_RestoreNetworkSharedWithTheNode_Fails(string network) =>
        Assert.True(Validation.Validate(null, new SandboxOptions { RestoreNetwork = network }).Failed);

    [Theory]
    [InlineData("none")]
    [InlineData("bridge")]
    [InlineData("packages")]
    public void Validate_RestoreNetworkOfItsOwn_Succeeds(string network) =>
        Assert.True(Validation.Validate(null, new SandboxOptions { RestoreNetwork = network }).Succeeded);

    [Fact]
    public void Validate_ZeroTestMemory_Fails() =>
        Assert.True(Validation.Validate(null, new SandboxOptions { TestMemoryBytes = 0 }).Failed);

    [Fact]
    public void Validate_DefaultTestTimeoutAboveTheMaximum_Fails() =>
        Assert.True(Validation.Validate(null, new SandboxOptions { DefaultTestTimeoutSeconds = 2000, MaxTestTimeoutSeconds = 1800 }).Failed);

    [Fact]
    public void Validate_ZeroProjectFiles_Fails() =>
        Assert.True(Validation.Validate(null, new SandboxOptions { MaxProjectFiles = 0 }).Failed);

    [Fact]
    public void Validate_EmptyDotnetImage_Fails() =>
        Assert.True(Validation.Validate(null, new SandboxOptions { DotnetImage = " " }).Failed);
}
