// score-inspector: look at plays from chat.
//
//   !recent <player|#id> [count]   the player's last plays (kept by this plugin from ScoreProcessedEvent)
//   !score <score id>              one play from its replay: map, mods, hits, combo, recomputed pp, when it was played
//   GET /inspect/replays/{id}      the replay as an .osr, for signed-in staff
//
// What it shows how to do: subscribe to events, register staff-only commands, keep small state (IPluginStateStore),
// use host services (IHostReplays, IHostBeatmaps, IHostPerformance, IHostAccounts, IHostPlayerView) and map an HTTP
// route that checks who's asking. Host services may be missing (null) on a bare host, so every use checks.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Examples.ScoreInspector;

/// <summary>Settings, from <c>Plugins:tomyum.example.score-inspector</c>.</summary>
public sealed class ScoreInspectorOptions
{
    /// <summary>Website groups that may use the commands and the download route (empty: admin, gmt, nat).</summary>
    public List<string> Groups { get; set; } = new();

    /// <summary>How many plays to remember per player.</summary>
    public int Keep { get; set; } = 20;

    /// <summary>The server's public URL (e.g. <c>https://c.example.com</c>), to put a download link in <c>!score</c>.</summary>
    public string? DownloadBase { get; set; }
}

/// <summary>One remembered play.</summary>
internal sealed record RecentPlay(
    long ScoreId, long BeatmapId, int Ruleset, double? Pp, string Rank, double Accuracy, int MaxCombo, bool Passed, bool Lazer, DateTimeOffset At);

/// <summary>The plugin.</summary>
public sealed class ScoreInspectorPlugin : IPlugin
{
    private static readonly string[] DefaultGroups = ["admin", "gmt", "nat"];

    private readonly List<IDisposable> _registrations = new();
    private readonly ConcurrentDictionary<long, bool> _namesKept = new();
    private readonly SemaphoreSlim _recentLock = new(1, 1);
    private IPluginContext? _context;
    private ScoreInspectorOptions _options = new();
    private HashSet<string> _groups = new(StringComparer.Ordinal);

    public PluginManifest Manifest { get; } = new(
        Id: "tomyum.example.score-inspector",
        Name: "Score inspector (example)",
        Version: "0.1.0",
        Authors: "Phapoom Saksri",
        Description: "!recent and !score for staff: a player's last plays, and one play read from its replay.");

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _options = context.Config.Bind<ScoreInspectorOptions>();
        _groups = new HashSet<string>(_options.Groups.Count > 0 ? _options.Groups : DefaultGroups, StringComparer.Ordinal);
        var staffOnly = new CommandOptions { RequiredGroups = _groups.ToList() };

        _registrations.Add(context.Events.Subscribe<ScoreProcessedEvent>(OnScoreProcessedAsync));
        _registrations.Add(context.Events.Subscribe<PlayerLoginEvent>((e, ct) => KeepNameAsync(e.UserId, e.Username, ct)));
        _registrations.Add(context.Commands.Register("recent", "A player's last plays", RecentAsync,
            staffOnly with { Usage = "<player|#id> [count]" }));
        _registrations.Add(context.Commands.Register("score", "One play, read from its replay", ScoreAsync,
            staffOnly with { Usage = "<score id>" }));
        _registrations.Add(context.Http.Map("GET", "/inspect/replays/{scoreId}", DownloadAsync, requireAuth: true));

        context.Logger.Info($"score-inspector: commands for {string.Join(", ", _groups)}; keeping {_options.Keep} plays per player");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        foreach (var r in _registrations)
            r.Dispose();
        _registrations.Clear();
        return Task.CompletedTask;
    }

    // ---- remembering plays ------------------------------------------------------------------------------------

    private async Task OnScoreProcessedAsync(ScoreProcessedEvent e, CancellationToken ct)
    {
        var state = _context!.State;
        var key = $"recent:{e.UserId}";
        var play = new RecentPlay(e.WebsiteScoreId, e.BeatmapId, e.RulesetId, e.Pp, e.Rank, e.Accuracy, e.MaxCombo, e.Passed, e.IsLazer, e.EndedAt);

        // Read-modify-write: the lock keeps two plays of this server from overwriting each other. (Several servers
        // sharing one Redis could still race; for an example that's fine.)
        await _recentLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plays = Parse(await state.GetAsync(key, ct).ConfigureAwait(false));
            plays.Insert(0, play);
            if (plays.Count > Math.Max(1, _options.Keep))
                plays.RemoveRange(_options.Keep, plays.Count - _options.Keep);
            await state.SetAsync(key, JsonSerializer.Serialize(plays), TimeSpan.FromDays(30), ct).ConfigureAwait(false);
        }
        finally
        {
            _recentLock.Release();
        }

        // So !recent can find lazer players by name too (stable players are kept at login).
        if (!_namesKept.ContainsKey(e.UserId) && _context.GetHostService<IHostAccounts>() is { } accounts
            && await accounts.GetAsync(e.UserId, ct).ConfigureAwait(false) is { } account)
            await KeepNameAsync(account.UserId, account.Username, ct).ConfigureAwait(false);
    }

    private async Task KeepNameAsync(long userId, string username, CancellationToken ct)
    {
        await _context!.State.SetAsync($"name:{SafeName(username)}", userId.ToString(CultureInfo.InvariantCulture), TimeSpan.FromDays(30), ct)
            .ConfigureAwait(false);
        _namesKept[userId] = true;
    }

    // ---- !recent ----------------------------------------------------------------------------------------------

    private async Task<string?> RecentAsync(CommandInvocation cmd, CancellationToken ct)
    {
        if (cmd.Arguments.Count == 0)
            return "Usage: !recent <player|#id> [count]";
        var count = cmd.Arguments.Count > 1 && int.TryParse(cmd.Arguments[^1], out var n) ? Math.Clamp(n, 1, 10) : 5;
        var who = string.Join(' ', cmd.Arguments.Take(cmd.Arguments.Count > 1 && int.TryParse(cmd.Arguments[^1], out _) ? cmd.Arguments.Count - 1 : cmd.Arguments.Count));
        var userId = await ResolveUserAsync(who, ct).ConfigureAwait(false);
        if (userId is null)
            return $"I don't know \"{who}\" yet (try #<id>, or wait until they log in or play).";

        var plays = Parse(await _context!.State.GetAsync($"recent:{userId}", ct).ConfigureAwait(false));
        if (plays.Count == 0)
            return $"No plays from #{userId} since this plugin started keeping them.";

        var beatmaps = _context.GetHostService<IHostBeatmaps>();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Last {Math.Min(count, plays.Count)} plays of #{userId}:");
        foreach (var p in plays.Take(count))
        {
            var map = beatmaps is null ? null : await beatmaps.GetAsync(p.BeatmapId, ct).ConfigureAwait(false);
            var title = map is null ? $"b/{p.BeatmapId}" : $"{map.Artist} - {map.Title} [{map.Version}]";
            var pp = p.Pp is { } v ? $"{v:0.##}pp" : "no pp";
            sb.Append('\n').Append(CultureInfo.InvariantCulture,
                $"#{p.ScoreId} {title} | {p.Rank} {p.Accuracy:P2} {p.MaxCombo}x {pp} | {(p.Lazer ? "lazer" : "stable")} {p.At:yyyy-MM-dd HH:mm}Z");
        }
        return sb.ToString();
    }

    private async Task<long?> ResolveUserAsync(string who, CancellationToken ct)
    {
        if (who.StartsWith('#') && long.TryParse(who.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return id;
        if (_context!.GetHostService<IHostPlayerView>()?.FindByUsername(who) is { } online)
            return online.UserId;
        var kept = await _context.State.GetAsync($"name:{SafeName(who)}", ct).ConfigureAwait(false);
        return long.TryParse(kept, NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : null;
    }

    // ---- !score -----------------------------------------------------------------------------------------------

    private async Task<string?> ScoreAsync(CommandInvocation cmd, CancellationToken ct)
    {
        if (cmd.Arguments.Count != 1 || !long.TryParse(cmd.Arguments[0].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var scoreId))
            return "Usage: !score <score id> (the website's, as in /scores/<id>)";
        if (_context!.GetHostService<IHostReplays>() is not { } replays)
            return "This server doesn't give plugins replays (is the plugin in Plugins:Trusted?).";

        var replay = await replays.GetByWebsiteScoreAsync(scoreId, ct).ConfigureAwait(false);
        if (replay is null)
            return $"Score {scoreId} has no replay (failed plays and plays outside the top ones often don't).";

        OsrInfo osr;
        try
        {
            osr = Osr.Read(replay.Data);
        }
        catch (InvalidDataException e)
        {
            return $"Score {scoreId}'s replay can't be read: {e.Message}.";
        }

        var map = _context.GetHostService<IHostBeatmaps>() is { } beatmaps
            ? await beatmaps.GetByMd5Async(osr.BeatmapMd5, ct).ConfigureAwait(false)
            : null;
        double? pp = null;
        if (map is not null && _context.GetHostService<IHostPerformance>() is { } performance)
        {
            pp = await performance.CalculateAsync(new HostPerformanceRequest(
                map.BeatmapId, osr.Mode, osr.Mods, osr.MaxCombo, osr.Count300, osr.Count100, osr.Count50, osr.CountMiss,
                osr.CountGeki, osr.CountKatu), ct).ConfigureAwait(false);
        }

        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var title = map is null ? $"a map not on this server ({osr.BeatmapMd5})" : $"{map.Artist} - {map.Title} [{map.Version}] (b/{map.BeatmapId}, {map.StarRating:0.00}*)";
        sb.Append(ic, $"Score {scoreId}: {osr.PlayerName} on {title} +{Osr.ModsText(osr.Mods)} ({Osr.ModeName(osr.Mode)})");
        var maxCombo = map is { MaxCombo: > 0 } ? $"/{map.MaxCombo}x" : string.Empty;
        sb.Append('\n').Append(ic,
            $"{osr.Accuracy:P2} {osr.MaxCombo}x{maxCombo}{(osr.Perfect ? " FC" : string.Empty)} | {osr.Count300}/{osr.Count100}/{osr.Count50}/{osr.CountMiss} miss | {osr.TotalScore:N0} | {(pp is { } v ? $"{v:0.##}pp recomputed" : "pp unknown")}");
        sb.Append('\n').Append(ic,
            $"Replay: {(replay.IsLazer ? "lazer" : $"stable b{osr.Version}")}, played {(osr.PlayedAt is { } at ? at.ToString("yyyy-MM-dd HH:mm:ss'Z'", ic) : "at an unknown time")}, {osr.FramesLength:N0} bytes of frames");
        if (!string.IsNullOrEmpty(_options.DownloadBase))
            sb.Append('\n').Append(ic, $"Download: {_options.DownloadBase!.TrimEnd('/')}/inspect/replays/{scoreId}");
        return sb.ToString();
    }

    // ---- GET /inspect/replays/{scoreId} -------------------------------------------------------------------------

    private async Task<PluginHttpResponse> DownloadAsync(PluginHttpRequest request, CancellationToken ct)
    {
        // requireAuth: the host already refused requests without a session; still check the caller is staff.
        if (request.AuthenticatedUserId is not { } caller
            || _context!.GetHostService<IHostAccounts>() is not { } accounts
            || await accounts.GetAsync(caller, ct).ConfigureAwait(false) is not { } account
            || !account.Groups.Overlaps(_groups))
            return Text(403, "Staff only.");

        if (!request.RouteValues.TryGetValue("scoreId", out var raw)
            || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var scoreId))
            return Text(400, "Bad score id.");
        if (_context.GetHostService<IHostReplays>() is not { } replays
            || await replays.GetByWebsiteScoreAsync(scoreId, ct).ConfigureAwait(false) is not { } replay)
            return Text(404, "No replay.");

        _context.Logger.Info($"score-inspector: {account.Username} downloaded the replay of score {scoreId}");
        return new PluginHttpResponse(200, "application/octet-stream", replay.Data, new Dictionary<string, string>
        {
            ["Content-Disposition"] = $"attachment; filename=\"{replay.FileName}\"",
            ["Cache-Control"] = "no-store",
        });
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static PluginHttpResponse Text(int status, string text) =>
        new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static List<RecentPlay> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new();
        try
        {
            return JsonSerializer.Deserialize<List<RecentPlay>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    // osu!'s "safe" name: lower case, spaces as underscores.
    private static string SafeName(string name) => name.Trim().ToLowerInvariant().Replace(' ', '_');
}
