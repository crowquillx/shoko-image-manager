using System.Reflection;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Enums;

namespace Shoko.ImagePlanner.Tests;

/// <summary>
/// Dynamic test double built on <see cref="DispatchProxy"/>. Property getters and setters route
/// to an in-memory dictionary, named methods can be stubbed, and every other member returns the
/// default value for its return type.
/// </summary>
public class DynamicFake : DispatchProxy
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<object?[], object?>> _behaviors = new(StringComparer.Ordinal);

    public DynamicFake WithValue(string propertyName, object? value)
    {
        _values[propertyName] = value;
        return this;
    }

    public DynamicFake WithBehavior(string methodName, Func<object?[], object?> behavior)
    {
        _behaviors[methodName] = behavior;
        return this;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;
        if (targetMethod.IsSpecialName && targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            return _values.TryGetValue(targetMethod.Name[4..], out var value) ? value : Default(targetMethod.ReturnType);
        if (targetMethod.IsSpecialName && targetMethod.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            _values[targetMethod.Name[4..]] = args is { Length: > 0 } ? args[0] : null;
            return null;
        }
        if (_behaviors.TryGetValue(targetMethod.Name, out var behavior))
            return behavior(args ?? []);
        return Default(targetMethod.ReturnType);
    }

    private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    public static T Create<T>(Action<DynamicFake> configure) where T : class
    {
        var proxy = DispatchProxy.Create<T, DynamicFake>();
        configure((DynamicFake)(object)proxy);
        return proxy;
    }
}

public sealed class InMemoryStateStore : IPluginStateStore
{
    private PluginState _state = new();

    public PluginState Load() => _state;

    public void Save(PluginState state) => _state = state;
}

public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, exception));
}

public sealed class FakeProvider : IImageProviderAdapter
{
    public required string Name { get; init; }
    public DataSource Source => DataSource.FanartTV;
    public Exception? Exception { get; init; }
    public IReadOnlyList<ProviderCandidate> Candidates { get; init; } = [];

    public Task<IReadOnlyList<ProviderCandidate>> GetCandidatesAsync(ProviderLookup lookup, CancellationToken cancellationToken)
        => Exception is null ? Task.FromResult(Candidates) : Task.FromException<IReadOnlyList<ProviderCandidate>>(Exception);
}

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public StubHttpMessageHandler(HttpResponseMessage response) => _response = response;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_response);
}
