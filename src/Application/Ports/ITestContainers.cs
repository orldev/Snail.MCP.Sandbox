namespace Snail.MCP.Sandbox.Application.Ports;

/// <summary>Builds and tests a project in a throwaway container, and removes it afterwards.</summary>
public interface ITestContainers
{
    /// <summary>What each stage did and what the reports counted, or why nothing ran — an unknown runtime, a missing image, an unreachable daemon.</summary>
    Task<Answer<TestExecution>> TestAsync(PackedProject project, Toolchain toolchain, TimeSpan timeout, CancellationToken cancellationToken);
}
