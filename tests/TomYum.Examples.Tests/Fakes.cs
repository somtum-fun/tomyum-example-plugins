// An in-memory stand-in for the TomYum plugin host: enough of IPluginContext to drive a plugin from a test.
// Handy in your own plugin's tests too.

using System.Collections.Concurrent;
using TomYum.Plugins.Abstractions;

namespace TomYum.Examples.Tests;

internal sealed class FakeContext : IPluginContext
{
    private readonly Dictionary<Type, object> _services = new();

    public FakeContext(object options) => Config = new FakeConfig(options);

    public IPluginConfig Config { get; }
    public FakeLogger Log { get; } = new();
    public IPluginLogger Logger => Log;
    public FakeEvents Bus { get; } = new();
    public IEventBus Events => Bus;
    public FakeCommands Cmds { get; } = new();
    public ICommandRegistry Commands => Cmds;
    public FakeHttp Routes { get; } = new();
    public IHttpRouteRegistry Http => Routes;
    public IPluginDataAccess? Data => null;
    public FakeState Store { get; } = new();
    public IPluginStateStore State => Store;
    public IPluginScheduler Scheduler => throw new NotSupportedException();

    public FakeContext With<T>(T service) where T : class
    {
        _services[typeof(T)] = service;
        return this;
    }

    public TService? GetHostService<TService>() where TService : class =>
        _services.TryGetValue(typeof(TService), out var s) ? (TService)s : null;

    public void PublishHostService<TService>(TService service) where TService : class => _services[typeof(TService)] = service;
}

internal sealed class FakeConfig(object options) : IPluginConfig
{
    public T? Get<T>(string key, T? defaultValue = default) => defaultValue;
    public TOptions Bind<TOptions>() where TOptions : new() => options is TOptions o ? o : new TOptions();
}

internal sealed class FakeLogger : IPluginLogger
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public void Info(string message) => Lines.Enqueue("info " + message);
    public void Warn(string message) => Lines.Enqueue("warn " + message);
    public void Error(string message, Exception? exception = null) => Lines.Enqueue($"error {message} {exception}");
}

internal sealed class FakeEvents : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Func<object, CancellationToken, Task>>> _handlers = new();
    public ConcurrentQueue<object> Published { get; } = new();

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IPluginEvent
    {
        var list = _handlers.GetOrAdd(typeof(TEvent), _ => new());
        Func<object, CancellationToken, Task> wrapped = (e, ct) => handler((TEvent)e, ct);
        lock (list)
            list.Add(wrapped);
        return new Disposer(() => { lock (list) list.Remove(wrapped); });
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IPluginEvent
    {
        Published.Enqueue(@event);
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            return;
        Func<object, CancellationToken, Task>[] copy;
        lock (list)
            copy = list.ToArray();
        foreach (var h in copy)
            await h(@event, cancellationToken);
    }

    /// <summary>Waits for a published event of type <typeparamref name="T"/> (plugins that work in the background).</summary>
    public async Task<T?> WaitFor<T>(Func<T, bool>? match = null, int ms = 3000) where T : class
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (Published.OfType<T>().FirstOrDefault(e => match?.Invoke(e) ?? true) is { } found)
                return found;
            await Task.Delay(20);
        }
        return null;
    }
}

internal sealed class FakeCommands : ICommandRegistry
{
    public Dictionary<string, (Func<CommandInvocation, CancellationToken, Task<string?>> Handler, CommandOptions Options)> All { get; } = new();

    public IDisposable Register(string name, string description, Func<CommandInvocation, CancellationToken, Task<string?>> handler) =>
        Register(name, description, handler, new CommandOptions());

    public IDisposable Register(string name, string description, Func<CommandInvocation, CancellationToken, Task<string?>> handler, CommandOptions options)
    {
        All[name] = (handler, options);
        return new Disposer(() => All.Remove(name));
    }

    public Task<string?> Run(string line, long caller = 1)
    {
        var parts = line.TrimStart('!').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All[parts[0]].Handler(new CommandInvocation(parts[0], parts[1..], line, caller, null), CancellationToken.None);
    }
}

internal sealed class FakeHttp : IHttpRouteRegistry
{
    public Dictionary<string, Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>>> All { get; } = new();

    public IDisposable Map(string method, string pattern, Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler, bool requireAuth = false, string? subdomain = null)
    {
        All[$"{method} {pattern}"] = handler;
        return new Disposer(() => All.Remove($"{method} {pattern}"));
    }
}

internal sealed class FakeState : IPluginStateStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new();
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
    public Task SetAsync(string key, string value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }
    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.TryRemove(key, out _));
}

internal sealed class FakeAccounts(params HostAccount[] accounts) : IHostAccounts
{
    public Task<HostAccount?> GetAsync(long userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(accounts.FirstOrDefault(a => a.UserId == userId));

    public static HostAccount Player(long id, string name, bool whitelisted = false, bool restricted = false, params string[] groups) =>
        new(id, name, "th", new HashSet<string>(groups), restricted, null, null, whitelisted, false);
}

internal sealed class FakeHostCommands : IHostCommands
{
    public ConcurrentQueue<string> Ran { get; } = new();
    public Task<string?> RunAsAsync(long asUserId, string commandLine, CancellationToken cancellationToken = default) => RunAsBotAsync(commandLine, cancellationToken);
    public Task<string?> RunAsBotAsync(string commandLine, CancellationToken cancellationToken = default)
    {
        Ran.Enqueue(commandLine);
        var name = commandLine.Split(' ')[1];
        return Task.FromResult<string?>($"Restricted {name}: ...");
    }
}

internal sealed class FakeReplays : IHostReplays
{
    public Dictionary<long, HostReplay> ByWebsiteScore { get; } = new();
    public Task<HostReplay?> GetByWebsiteScoreAsync(long websiteScoreId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ByWebsiteScore.TryGetValue(websiteScoreId, out var r) ? r : null);
    public Task<HostReplay?> GetByTomYumScoreAsync(long scoreId, CancellationToken cancellationToken = default) => Task.FromResult<HostReplay?>(null);
}

internal sealed class FakeBeatmaps(params HostBeatmap[] maps) : IHostBeatmaps
{
    public Task<HostBeatmap?> GetAsync(long beatmapId, CancellationToken cancellationToken = default) => Task.FromResult(maps.FirstOrDefault(m => m.BeatmapId == beatmapId));
    public Task<HostBeatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult(maps.FirstOrDefault(m => m.Md5 == md5));
    public Task<byte[]?> GetOsuFileAsync(long beatmapId, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>("osu file format v14\n"u8.ToArray());
}

internal sealed class FakePerformance(double pp) : IHostPerformance
{
    public HostPerformanceRequest? Last { get; private set; }
    public Task<double?> CalculateAsync(HostPerformanceRequest request, CancellationToken cancellationToken = default)
    {
        Last = request;
        return Task.FromResult<double?>(pp);
    }
    public Task<IReadOnlyList<double>?> ForAccuraciesAsync(long beatmapId, int mode, int mods, IReadOnlyList<double> accuracies, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<double>?>(null);
    public Task<double?> StarRatingAsync(long beatmapId, int mode, int mods, CancellationToken cancellationToken = default) => Task.FromResult<double?>(null);
}

internal sealed class Disposer(Action action) : IDisposable
{
    public void Dispose() => action();
}
