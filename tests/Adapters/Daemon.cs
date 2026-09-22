using System.Reflection;
using Docker.DotNet;

namespace Snail.MCP.Sandbox.Tests.Adapters;

/// <summary>A Docker daemon that answers only what a test scripts, and remembers every operation asked of it, in order.</summary>
/// <remarks>
/// A proxy rather than a hand-written fake: the client's operation interfaces hold over thirty methods, and a
/// test that forgot to script one should fail by naming it, not run into a default that looks like an answer.
/// </remarks>
public sealed class Daemon
{
    private readonly Dictionary<string, Func<object?[], object?>> _answers = [];

    public Daemon()
    {
        var system = Proxy<ISystemOperations>();
        var containers = Proxy<IContainerOperations>();
        var exec = Proxy<IExecOperations>();
        var images = Proxy<IImageOperations>();

        Client = Proxy<IDockerClient>();
        _answers["get_System"] = _ => system;
        _answers["get_Containers"] = _ => containers;
        _answers["get_Exec"] = _ => exec;
        _answers["get_Images"] = _ => images;
    }

    public IDockerClient Client { get; }

    public List<string> Calls { get; } = [];

    public Daemon Answer(string operation, Func<object?[], object?> answer)
    {
        _answers[operation] = answer;

        return this;
    }

    internal object? Invoke(string operation, object?[] arguments)
    {
        if (!operation.StartsWith("get_", StringComparison.Ordinal))
            Calls.Add(operation);

        return _answers.TryGetValue(operation, out var answer)
            ? answer(arguments)
            : throw new NotSupportedException($"{operation} was not scripted for this test");
    }

    private T Proxy<T>() where T : class
    {
        var proxy = DispatchProxy.Create<T, Scripted>();
        ((Scripted)(object)proxy).Daemon = this;

        return proxy;
    }
}

/// <summary>The proxy half of <see cref="Daemon"/>: every call on a Docker interface goes to the script.</summary>
public class Scripted : DispatchProxy
{
    internal Daemon Daemon { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        Daemon.Invoke(targetMethod!.Name, args ?? []);
}
