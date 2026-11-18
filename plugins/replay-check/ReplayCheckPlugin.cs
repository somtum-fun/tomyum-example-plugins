// replay-check: look at each new play's replay, and tell staff about the suspicious ones. It never restricts.
//
// Two checks:
//   1. Copied replay (here, no outside service): the compressed frames are hashed; the same frames under a second
//      player's score mean one of them was copied. Catches straight copies, not edited ones.
//   2. An outside analyzer (optional, "analyzerUrl"): the replay and the map's .osu are POSTed as JSON to a service
//      that answers with a suspicion (0-1) and its reasons. analyzer/ in this repo is one built on circleguard
//      (unstable rate, frame times, snaps). Anything that speaks the same JSON works.
//
// Plays are queued and checked in the background, a few at a time, so a burst of scores doesn't hold up the
// server. The plugin also offers IScoreCheckService, so another plugin (the Discord bot's /scan) can ask for a
// check of any score; a check asked for that way returns its finding whatever the suspicion.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Examples.ReplayCheck;

/// <summary>Settings, from <c>Plugins:tomyum.example.replay-check</c>.</summary>
public sealed class ReplayCheckOptions
{
    /// <summary>The analyzer's URL (e.g. <c>http://analyzer:8600/analyze</c>); unset: copied-replay check only.</summary>
    public string? AnalyzerUrl { get; set; }

    /// <summary>Sent as <c>Authorization: Bearer ...</c> to the analyzer, if set. Put it in an env var, not a file in git.</summary>
    public string? AnalyzerKey { get; set; }

    /// <summary>Tell staff from this suspicion up (0-1).</summary>
    public double AlertAt { get; set; } = 0.8;

    /// <summary>Only plays with at least this much pp are sent to the analyzer (0: all).</summary>
    public double MinPp { get; set; }

    /// <summary>Rulesets checked (website ids); empty: osu! only (circleguard knows only osu!).</summary>
    public List<int> Rulesets { get; set; } = new();

    /// <summary>Only plays that count on leaderboards.</summary>
    public bool RankedOnly { get; set; } = true;

    /// <summary>How many plays are checked at once.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>How long one analyzer call may take.</summary>
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>What the plugin sends the analyzer.</summary>
public sealed record AnalyzerRequest(
    [property: JsonPropertyName("score_id")] long ScoreId,
    [property: JsonPropertyName("user_id")] long UserId,
    [property: JsonPropertyName("beatmap_id")] long BeatmapId,
    [property: JsonPropertyName("ruleset_id")] int RulesetId,
    [property: JsonPropertyName("replay")] string Replay,
    [property: JsonPropertyName("beatmap")] string? Beatmap);

/// <summary>What the analyzer answers.</summary>
public sealed record AnalyzerResponse(
    [property: JsonPropertyName("suspicion")] double Suspicion,
    [property: JsonPropertyName("reasons")] List<string>? Reasons,
    [property: JsonPropertyName("fields")] Dictionary<string, string>? Fields);

/// <summary>The plugin.</summary>
public sealed class ReplayCheckPlugin : IPlugin, IScoreCheckService
{
    private readonly List<IDisposable> _registrations = new();
    private readonly Channel<ScoreProcessedEvent> _queue = Channel.CreateBounded<ScoreProcessedEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _workers = new();
    private IPluginContext? _context;
    private ReplayCheckOptions _options = new();
    private HashSet<int> _rulesets = new();
    private HttpClient? _http;

    public PluginManifest Manifest { get; } = new(
        Id: "tomyum.example.replay-check",
        Name: "Replay check (example)",
        Version: "0.1.0",
        Authors: "Phapoom Saksri",
        Description: "Checks new plays' replays (copied replays, and an outside analyzer such as circleguard). Only tells staff.");

    /// <inheritdoc/>
    public string Name => "replay-check";

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _options = context.Config.Bind<ReplayCheckOptions>();
        _rulesets = new HashSet<int>(_options.Rulesets.Count > 0 ? _options.Rulesets : [0]);
        if (!string.IsNullOrEmpty(_options.AnalyzerUrl))
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)) };
            if (!string.IsNullOrEmpty(_options.AnalyzerKey))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AnalyzerKey);
        }

        _registrations.Add(context.Events.Subscribe<ScoreProcessedEvent>((e, _) =>
        {
            if (e.Passed && (e.Ranked || !_options.RankedOnly) && _rulesets.Contains(e.RulesetId))
                _queue.Writer.TryWrite(e);
            return Task.CompletedTask;
        }));
        for (int i = 0; i < Math.Clamp(_options.Concurrency, 1, 8); i++)
            _workers.Add(Task.Run(() => WorkAsync(_stop.Token)));
        context.PublishHostService<IScoreCheckService>(this);

        if (context.GetHostService<IHostReplays>() is null)
            context.Logger.Warn("replay-check: this server doesn't give plugins replays (Plugins:Trusted?): nothing will be checked");
        context.Logger.Info($"replay-check: rulesets {string.Join(",", _rulesets)}, analyzer {(_http is null ? "off" : _options.AnalyzerUrl)}, alert at {_options.AlertAt:0.##}");
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        foreach (var r in _registrations)
            r.Dispose();
        _registrations.Clear();
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
        }
        _http?.Dispose();
        _stop.Dispose();
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var result = await CheckCoreAsync(e.WebsiteScoreId, e.Pp, "new play", null, ct).ConfigureAwait(false);
                    if (result.Alert is { } alert && alert.Severity >= _options.AlertAt * 100)
                        await _context!.Events.PublishAsync(new StaffAlertEvent(alert), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _context!.Logger.Error($"replay-check: score {e.WebsiteScoreId} failed", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc/>
    public Task<ScoreCheckResult> CheckAsync(ulong scoreId, string requestedBy, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        CheckCoreAsync((long)scoreId, null, $"asked for by {requestedBy}", progress, cancellationToken);

    private async Task<ScoreCheckResult> CheckCoreAsync(long scoreId, double? pp, string why, IProgress<string>? progress, CancellationToken ct)
    {
        var context = _context!;
        if (context.GetHostService<IHostReplays>() is not { } replays)
            return new(null, "this server doesn't give plugins replays");

        progress?.Report("fetching the replay");
        var replay = await replays.GetByWebsiteScoreAsync(scoreId, ct).ConfigureAwait(false);
        if (replay is null)
            return new(null, $"score {scoreId} has no replay");
        OsrInfo osr;
        try
        {
            osr = Osr.Read(replay.Data);
        }
        catch (InvalidDataException e)
        {
            return new(null, $"the replay can't be read: {e.Message}");
        }

        var ic = CultureInfo.InvariantCulture;
        var reasons = new List<string>();
        var fields = new List<StaffAlertField>();
        double suspicion = 0;

        // 1. Copied replay.
        progress?.Report("looking for the same replay under another score");
        var hash = Convert.ToHexString(SHA256.HashData(replay.Data.AsSpan(osr.FramesOffset, osr.FramesLength)));
        var mine = string.Create(ic, $"{scoreId}:{replay.UserId}");
        var first = await context.State.GetAsync($"frames:{hash}", ct).ConfigureAwait(false);
        if (first is null)
            await context.State.SetAsync($"frames:{hash}", mine, null, ct).ConfigureAwait(false);
        else if (first != mine && first.Split(':') is [var otherScore, var otherUser] && otherUser != replay.UserId.ToString(ic))
        {
            suspicion = 1;
            reasons.Add($"Same replay frames as score {otherScore} by player #{otherUser}: one of them is a copy.");
        }

        // 2. The outside analyzer.
        var beatmapId = 0L;
        if (_http is not null && (pp is null || pp >= _options.MinPp))
        {
            progress?.Report("sending it to the analyzer");
            var beatmaps = context.GetHostService<IHostBeatmaps>();
            var map = beatmaps is null ? null : await beatmaps.GetByMd5Async(osr.BeatmapMd5, ct).ConfigureAwait(false);
            beatmapId = map?.BeatmapId ?? 0;
            var osu = map is null || beatmaps is null ? null : await beatmaps.GetOsuFileAsync(map.BeatmapId, ct).ConfigureAwait(false);
            var request = new AnalyzerRequest(scoreId, replay.UserId, beatmapId, osr.Mode, Convert.ToBase64String(replay.Data),
                osu is null ? null : Convert.ToBase64String(osu));
            try
            {
                using var response = await _http.PostAsJsonAsync(_options.AnalyzerUrl, request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var answer = await response.Content.ReadFromJsonAsync<AnalyzerResponse>(ct).ConfigureAwait(false);
                if (answer is not null)
                {
                    suspicion = Math.Max(suspicion, Math.Clamp(answer.Suspicion, 0, 1));
                    reasons.AddRange(answer.Reasons ?? []);
                    fields.AddRange((answer.Fields ?? new()).Select(f => new StaffAlertField(f.Key, f.Value)));
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                context.Logger.Warn($"replay-check: the analyzer failed on score {scoreId}: {e.Message}");
                if (reasons.Count == 0)
                    return new(null, $"the analyzer failed: {e.Message}");
            }
        }

        var username = context.GetHostService<IHostAccounts>() is { } accounts
            && await accounts.GetAsync(replay.UserId, ct).ConfigureAwait(false) is { } account
            ? account.Username
            : osr.PlayerName;
        fields.Insert(0, new StaffAlertField("Mods", Osr.ModsText(osr.Mods)));
        fields.Insert(1, new StaffAlertField("Accuracy", osr.Accuracy.ToString("P2", ic)));
        var alert = new StaffAlert(
            Source: "replay-check",
            UserId: (int)replay.UserId,
            Username: username,
            Headline: reasons.Count == 0
                ? "Nothing found in the replay"
                : string.Create(ic, $"Suspicious replay ▸ {suspicion * 100:0}/100"),
            Details: reasons.Count == 0 ? "No check found anything." : string.Join('\n', reasons),
            Severity: (int)Math.Round(suspicion * 100),
            ScoreId: (ulong)scoreId)
        {
            Fields = fields,
            Footer = $"replay-check, {why}. It doesn't restrict: look at the play before doing anything.",
        };
        return new(alert, null);
    }
}
