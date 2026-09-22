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
}
