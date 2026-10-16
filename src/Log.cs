namespace Snail.MCP.Sandbox;

/// <summary>What the server says about the daemon it runs code through, the containers it makes and the restore proxy.</summary>
/// <remarks>
/// Generated rather than written through the logging extensions: a line costs nothing while its level is off, where the
/// extensions boxed and formatted their arguments first. The messages are the ones the server has always written.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "A run did not complete through the Docker daemon at {Endpoint}")]
    internal static partial void RunNotCompleted(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The Docker daemon at {Endpoint} did not answer a diagnosis")]
    internal static partial void DiagnosisNotAnswered(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The node holds no {Image}; {Toolchain} projects cannot be tested")]
    internal static partial void ToolchainImageMissing(ILogger logger, string image, Toolchain toolchain);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "The Docker daemon has no runtime {Runtime}, only {Known}; every run is refused")]
    internal static partial void RuntimeMissing(ILogger logger, string runtime, string known);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Image {Image} is not on this node; every run fails until it is")]
    internal static partial void ImageMissing(ILogger logger, string image);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Docker warned while creating {Container}: {Warning}")]
    internal static partial void CreationWarned(ILogger logger, string container, string warning);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "A run in {Container} passed its {Seconds}s and was killed")]
    internal static partial void RunKilled(ILogger logger, string container, double seconds);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "Container {Container} was already gone or being removed")]
    internal static partial void ContainerAlreadyGone(ILogger logger, string container);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Container {Container} was not removed; it stops itself when its own clock runs out")]
    internal static partial void ContainerNotRemoved(ILogger logger, Exception exception, string container);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "A test run did not complete through the Docker daemon at {Endpoint}")]
    internal static partial void TestRunNotCompleted(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Restores reach the network through the proxy on port {Port}")]
    internal static partial void ProxyListening(ILogger logger, int port);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "A restore's connection through the proxy ended")]
    internal static partial void ProxyConnectionEnded(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Removed {Count} run containers left by an earlier process of this server")]
    internal static partial void LeftoversRemoved(ILogger logger, int count);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "Leftover run containers were not swept; the Docker daemon did not answer")]
    internal static partial void LeftoversNotSwept(ILogger logger, Exception exception);
}
