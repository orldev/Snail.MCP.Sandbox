using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Tests.Chaos;

/// <summary>The settings against values that are numbers to the binder and limits to nobody.</summary>
public sealed class SandboxOptionsChaosTests
{
    /// <summary>
    /// Breaks the number: a CPU limit that is not a number passes every "positive" check, and NaN cast to nanoseconds
    /// is zero — a run with no CPU limit under a setting that claims one. Infinity saturates to a value the daemon
    /// refuses, which at least fails loudly; both are refused at start.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Validate_CpusNotFinite_IsRefused(double cpus) =>
        Assert.True(new SandboxOptionsValidation().Validate(null, new SandboxOptions { Cpus = cpus }).Failed);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Validate_TestCpusNotFinite_IsRefused(double cpus) =>
        Assert.True(new SandboxOptionsValidation().Validate(null, new SandboxOptions { TestCpus = cpus }).Failed);
}
