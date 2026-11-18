// pp-cap: a play worth more pp than its mode's cap is something staff should look at.
//
// osu!(stable) plays come in as ScoreSubmittedEvent (with Relax/Autopilot in GameMode), osu!lazer plays as
// ScoreProcessedEvent once the website has worked out their pp. Over the cap, the plugin raises a StaffAlertEvent
// (the Discord plugin posts it), and only with "action: restrict" does it restrict, through BanchoBot's own !restrict,
// so the staff log and the in-game effect are the same as a staff member typing it.
//
// Left alone: failed plays, restricted players, players staff put on the anti-cheat whitelist, and the exempt groups.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Examples.PpCap;

/// <summary>Settings, from <c>Plugins:tomyum.example.pp-cap</c>.</summary>
public sealed class PpCapOptions
{
    /// <summary>
    /// The caps by mode: <c>osu</c>, <c>taiko</c>, <c>catch</c>, <c>mania</c>, <c>osu-relax</c>, <c>taiko-relax</c>,
    /// <c>catch-relax</c>, <c>osu-autopilot</c>, or a custom lazer ruleset's id (<c>10</c>, ...). A mode with no cap isn't checked.
    /// </summary>
    public Dictionary<string, double> Caps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>alert</c> (tell staff, the default) or <c>restrict</c> (restrict, then tell staff).</summary>
    public string Action { get; set; } = "alert";

    /// <summary>Website groups never checked (empty: admin, gmt, nat, dev).</summary>
    public List<string> ExemptGroups { get; set; } = new();

    /// <summary>If set, an in-game notification to the player when they go over (e.g. "Your play is waiting for staff review").</summary>
    public string? NotifyPlayer { get; set; }
}

/// <summary>What <see cref="PpCapRule.Check"/> decided.</summary>
/// <param name="Over">Whether the play is over its cap and should be acted on.</param>
/// <param name="ModeKey">The mode's key in <see cref="PpCapOptions.Caps"/>.</param>
/// <param name="Cap">The cap, when there is one.</param>
/// <param name="Skipped">Why it's left alone, or null.</param>
public sealed record PpCapDecision(bool Over, string ModeKey, double? Cap, string? Skipped);

/// <summary>The rule itself, free of the host so it can be tested on its own.</summary>
public static class PpCapRule
{
    private static readonly string[] BaseModes = ["osu", "taiko", "catch", "mania"];

    /// <summary>The cap key for an osu!(stable) combined game mode (0-3 vanilla, +4 Relax, +8 Autopilot).</summary>
    public static string StableModeKey(int gameMode)
    {
        var baseMode = BaseModes[Math.Clamp(gameMode % 4, 0, 3)];
        return gameMode >= 8 ? $"{baseMode}-autopilot" : gameMode >= 4 ? $"{baseMode}-relax" : baseMode;
    }

    /// <summary>The cap key for a website ruleset id (0-3, or a custom ruleset's id).</summary>
    public static string RulesetKey(int rulesetId) =>
        rulesetId is >= 0 and <= 3 ? BaseModes[rulesetId] : rulesetId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether a play should be acted on.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="exemptGroups">The exempt groups (already defaulted).</param>
    /// <param name="modeKey">The play's mode key.</param>
    /// <param name="pp">The play's pp (null: none).</param>
    /// <param name="passed">Whether it passed.</param>
    /// <param name="account">The player's account, when known.</param>
    public static PpCapDecision Check(PpCapOptions options, IReadOnlySet<string> exemptGroups, string modeKey, double? pp, bool passed, HostAccount? account)
    {
        if (!options.Caps.TryGetValue(modeKey, out var cap) || cap <= 0)
            return new(false, modeKey, null, "no cap for this mode");
        if (!passed)
            return new(false, modeKey, cap, "failed play");
        if (pp is not { } value || value <= cap)
            return new(false, modeKey, cap, "under the cap");
        if (account is null)
            return new(false, modeKey, cap, "unknown account");
        if (account.IsBot)
            return new(false, modeKey, cap, "bot");
        if (account.Restricted)
            return new(false, modeKey, cap, "already restricted");
        if (account.Whitelisted)
            return new(false, modeKey, cap, "whitelisted");
        if (account.Groups.Overlaps(exemptGroups))
            return new(false, modeKey, cap, "exempt group");
        return new(true, modeKey, cap, null);
    }
}

/// <summary>The plugin.</summary>
public sealed class PpCapPlugin : IPlugin
{
    private static readonly string[] DefaultExempt = ["admin", "gmt", "nat", "dev"];

    private readonly List<IDisposable> _registrations = new();
    private IPluginContext? _context;
    private PpCapOptions _options = new();
    private HashSet<string> _exempt = new(StringComparer.Ordinal);
    private bool _restrict;

    public PluginManifest Manifest { get; } = new(
        Id: "tomyum.example.pp-cap",
        Name: "pp cap (example)",
        Version: "0.1.0",
        Authors: "Phapoom Saksri",
        Description: "Tells staff (or restricts, if set to) when a play goes over a per-mode pp cap.");

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _options = context.Config.Bind<PpCapOptions>();
        _options.Caps = new Dictionary<string, double>(_options.Caps, StringComparer.OrdinalIgnoreCase);
        _exempt = new HashSet<string>(_options.ExemptGroups.Count > 0 ? _options.ExemptGroups : DefaultExempt, StringComparer.Ordinal);
        _restrict = string.Equals(_options.Action, "restrict", StringComparison.OrdinalIgnoreCase);

        // Both handlers return at once and do the work on the thread pool: ScoreSubmittedEvent is raised on the
        // score submission path, and a player shouldn't wait for an account lookup.
        _registrations.Add(context.Events.Subscribe<ScoreSubmittedEvent>((e, _) =>
        {
            if (e.PlayerRestricted)
                return Task.CompletedTask;
            Background(() => HandleAsync("stable", e.ScoreId, e.UserId, e.BeatmapId, PpCapRule.StableModeKey(e.GameMode), e.Pp, e.Passed, e.BeatmapTitle));
            return Task.CompletedTask;
        }));
        _registrations.Add(context.Events.Subscribe<ScoreProcessedEvent>((e, _) =>
        {
            if (!e.IsLazer) // stable plays were already checked as ScoreSubmittedEvent
                return Task.CompletedTask;
            Background(() => HandleAsync("lazer", e.WebsiteScoreId, e.UserId, e.BeatmapId, PpCapRule.RulesetKey(e.RulesetId), e.Pp, e.Passed, string.Empty));
            return Task.CompletedTask;
        }));

        var caps = _options.Caps.Count == 0 ? "none (nothing is checked)" : string.Join(", ", _options.Caps.Select(c => $"{c.Key} {c.Value:0}pp"));
        context.Logger.Info($"pp-cap: caps {caps}; action {(_restrict ? "restrict" : "alert")}; exempt {string.Join(", ", _exempt)}");
        if (_restrict && context.GetHostService<IHostCommands>() is null)
            context.Logger.Warn("pp-cap: action is restrict, but this server doesn't give plugins BanchoBot's commands (Plugins:Trusted?): alerting only");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        foreach (var r in _registrations)
            r.Dispose();
        _registrations.Clear();
        return Task.CompletedTask;
    }

    private void Background(Func<Task> work) => _ = Task.Run(async () =>
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _context!.Logger.Error("pp-cap: checking a play failed", e);
        }
    });

    private async Task HandleAsync(string client, long scoreId, long userId, long beatmapId, string modeKey, double? pp, bool passed, string title)
    {
        var context = _context!;
        // Cheap checks first, so most plays never cost an account lookup.
        if (!passed || pp is not { } value || !_options.Caps.TryGetValue(modeKey, out var cap) || value <= cap)
            return;

        // Once per play, even if an event comes twice.
        var seen = $"seen:{client}:{scoreId}";
        if (await context.State.GetAsync(seen).ConfigureAwait(false) is not null)
            return;
        await context.State.SetAsync(seen, "1", TimeSpan.FromDays(7)).ConfigureAwait(false);

        var account = context.GetHostService<IHostAccounts>() is { } accounts ? await accounts.GetAsync(userId).ConfigureAwait(false) : null;
        var decision = PpCapRule.Check(_options, _exempt, modeKey, pp, passed, account);
        if (!decision.Over || account is null)
        {
            context.Logger.Info($"pp-cap: {client} score {scoreId} by #{userId}: {value:0.##}pp over {cap:0}pp, left alone ({decision.Skipped})");
            return;
        }

        var ic = CultureInfo.InvariantCulture;
        var restricted = false;
        if (_restrict && context.GetHostService<IHostCommands>() is { } commands)
        {
            var reason = string.Create(ic, $"pp cap: {value:0}pp over the {modeKey} cap of {cap:0}pp (score {scoreId})");
            var reply = await commands.RunAsBotAsync($"!restrict {account.Username.Replace(' ', '_')} {reason}").ConfigureAwait(false);
            restricted = reply?.StartsWith("Restricted", StringComparison.Ordinal) == true;
            context.Logger.Info($"pp-cap: !restrict {account.Username}: {reply}");
        }

        if (!string.IsNullOrEmpty(_options.NotifyPlayer))
            context.GetHostService<IHostMessenger>()?.SendNotification(userId, _options.NotifyPlayer!);

        var alert = new StaffAlert(
            Source: "pp-cap",
            UserId: (int)userId,
            Username: account.Username,
            Headline: string.Create(ic, $"Over the pp cap ▸ {value:0}pp ({modeKey}, cap {cap:0}pp)"),
            Details: string.IsNullOrEmpty(title) ? $"Map b/{beatmapId}" : $"{title} (b/{beatmapId})",
            Severity: restricted ? 80 : 60,
            ScoreId: client == "lazer" ? (ulong)scoreId : null,
            Restricted: restricted)
        {
            Fields =
            [
                new StaffAlertField("pp", value.ToString("0.##", ic)),
                new StaffAlertField("Cap", cap.ToString("0", ic)),
                new StaffAlertField("Client", client),
                new StaffAlertField("Score", scoreId.ToString(ic)),
            ],
            Footer = restricted ? "Restricted by pp-cap; unrestrict if the play is fine." : "Not restricted: pp-cap only alerts.",
        };
        await context.Events.PublishAsync(new StaffAlertEvent(alert)).ConfigureAwait(false);
        context.Logger.Info($"pp-cap: {client} score {scoreId} by {account.Username}: {value:0.##}pp over {cap:0}pp ({(restricted ? "restricted" : "alerted")})");
    }
}
