using System.Text;
using TomYum.Examples.PpCap;
using TomYum.Examples.ReplayCheck;
using TomYum.Examples.ScoreInspector;
using TomYum.Plugins.Abstractions;
using Xunit;
using InspectorOsr = TomYum.Examples.ScoreInspector.Osr;

namespace TomYum.Examples.Tests;

/// <summary>Builds .osr files the way osu! writes them.</summary>
internal static class OsrBuilder
{
    public const string MapMd5 = "c48478f7a85e2b2368f1d383156319a0";

    public static byte[] Build(byte[] frames, int mods = 72, string player = "someone", int version = 20240123, long onlineId = 1234)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write((byte)0);
        w.Write(version);
        Str(w, MapMd5);
        Str(w, player);
        Str(w, new string('0', 32));
        foreach (var n in new ushort[] { 1663, 29, 0, 197, 23, 1 })
            w.Write(n);
        w.Write(568680);
        w.Write((ushort)1873);
        w.Write((byte)0);
        w.Write(mods);
        w.Write((byte)0); // no life bar
        w.Write(new DateTime(2024, 3, 12, 5, 12, 35, DateTimeKind.Utc).Ticks);
        w.Write(frames.Length);
        w.Write(frames);
        w.Write(onlineId);
        return ms.ToArray();
    }

    private static void Str(BinaryWriter w, string s)
    {
        w.Write((byte)0x0b);
        w.Write(s); // 7-bit length prefix, as osu! writes it
    }
}

public class OsrTests
{
    [Fact]
    public void ReadsTheHeader()
    {
        var frames = new byte[] { 1, 2, 3, 4, 5 };
        var osr = InspectorOsr.Read(OsrBuilder.Build(frames, mods: 72, player: "peppy"));
        Assert.Equal(0, osr.Mode);
        Assert.Equal(OsrBuilder.MapMd5, osr.BeatmapMd5);
        Assert.Equal("peppy", osr.PlayerName);
        Assert.Equal((1663, 29, 0, 1), (osr.Count300, osr.Count100, osr.Count50, osr.CountMiss));
        Assert.Equal(1873, osr.MaxCombo);
        Assert.Equal(5, osr.FramesLength);
        Assert.Equal(1234, osr.OnlineScoreId);
        Assert.Equal(new DateTimeOffset(2024, 3, 12, 5, 12, 35, TimeSpan.Zero), osr.PlayedAt);
        Assert.Equal("HDDT", InspectorOsr.ModsText(osr.Mods));
        Assert.Equal(0.9880, osr.Accuracy, 4); // (300*1663 + 100*29) / (300*1693)
    }

    [Theory]
    [InlineData(0, "NM")]
    [InlineData(576, "NC")]       // NC carries DT's bit too
    [InlineData(16416, "PF")]     // PF carries SD's bit too
    [InlineData(128 | 8 | 64, "HDDTRX")]
    public void ModsText(int mods, string text) => Assert.Equal(text, InspectorOsr.ModsText(mods));

    [Fact]
    public void RefusesWhatIsNotAReplay()
    {
        Assert.Throws<InvalidDataException>(() => InspectorOsr.Read([9, 9, 9]));
        Assert.Throws<InvalidDataException>(() => InspectorOsr.Read(new byte[200]));
    }
}

public class PpCapTests
{
    private static readonly HashSet<string> Exempt = ["admin", "gmt"];
    private static readonly PpCapOptions Options = new() { Caps = new(StringComparer.OrdinalIgnoreCase) { ["osu"] = 700, ["osu-relax"] = 1500 } };

    [Theory]
    [InlineData(0, "osu")]
    [InlineData(3, "mania")]
    [InlineData(4, "osu-relax")]
    [InlineData(5, "taiko-relax")]
    [InlineData(8, "osu-autopilot")]
    public void StableModeKeys(int gameMode, string key) => Assert.Equal(key, PpCapRule.StableModeKey(gameMode));

    [Fact]
    public void Decides()
    {
        var player = FakeAccounts.Player(5, "someone");
        Assert.True(PpCapRule.Check(Options, Exempt, "osu", 701, true, player).Over);
        Assert.Equal("under the cap", PpCapRule.Check(Options, Exempt, "osu", 700, true, player).Skipped);
        Assert.Equal("failed play", PpCapRule.Check(Options, Exempt, "osu", 900, false, player).Skipped);
        Assert.Equal("no cap for this mode", PpCapRule.Check(Options, Exempt, "taiko", 9000, true, player).Skipped);
        Assert.Equal("whitelisted", PpCapRule.Check(Options, Exempt, "osu", 900, true, FakeAccounts.Player(5, "a", whitelisted: true)).Skipped);
        Assert.Equal("exempt group", PpCapRule.Check(Options, Exempt, "osu", 900, true, FakeAccounts.Player(5, "a", false, false, "gmt")).Skipped);
        Assert.Equal("already restricted", PpCapRule.Check(Options, Exempt, "osu", 900, true, FakeAccounts.Player(5, "a", restricted: true)).Skipped);
        Assert.False(PpCapRule.Check(Options, Exempt, "osu-relax", 1200, true, player).Over);
    }

    private static ScoreSubmittedEvent Stable(long scoreId, long userId, double pp, byte gameMode = 0) =>
        new(scoreId, userId, 1, 1, OsrBuilder.MapMd5, gameMode, 0, 0.99, 1_000_000, 500, 0, "S", pp, true, "b20240123") { BeatmapTitle = "A - B [C]" };

    [Fact]
    public async Task RestrictsWhenToldTo()
    {
        var bot = new FakeHostCommands();
        var ctx = new FakeContext(new PpCapOptions { Caps = new() { ["osu"] = 700 }, Action = "restrict" })
            .With<IHostAccounts>(new FakeAccounts(FakeAccounts.Player(5, "some one")))
            .With<IHostCommands>(bot);
        var plugin = new PpCapPlugin();
        await plugin.InitializeAsync(ctx);

        await ctx.Bus.PublishAsync(Stable(42, 5, 812.4));
        var alert = await ctx.Bus.WaitFor<StaffAlertEvent>();

        Assert.NotNull(alert);
        Assert.True(alert!.Alert.Restricted);
        Assert.Equal("pp-cap", alert.Alert.Source);
        Assert.StartsWith("!restrict some_one pp cap: 812pp over the osu cap of 700pp (score 42)", Assert.Single(bot.Ran));

        // The same play again (a repeated event) isn't acted on twice.
        await ctx.Bus.PublishAsync(Stable(42, 5, 812.4));
        await Task.Delay(200);
        Assert.Single(bot.Ran);
    }

    [Fact]
    public async Task OnlyAlertsByDefault_AndLeavesTheWhitelistedAlone()
    {
        var bot = new FakeHostCommands();
        var ctx = new FakeContext(new PpCapOptions { Caps = new() { ["osu-relax"] = 1000 } })
            .With<IHostAccounts>(new FakeAccounts(FakeAccounts.Player(5, "a"), FakeAccounts.Player(6, "b", whitelisted: true)))
            .With<IHostCommands>(bot);
        await new PpCapPlugin().InitializeAsync(ctx);

        await ctx.Bus.PublishAsync(Stable(1, 6, 1400, gameMode: 4));
        await ctx.Bus.PublishAsync(Stable(2, 5, 1400, gameMode: 4));
        var alert = await ctx.Bus.WaitFor<StaffAlertEvent>();

        Assert.Equal(5, alert!.Alert.UserId);
        Assert.False(alert.Alert.Restricted);
        Assert.Empty(bot.Ran);
        await Task.Delay(200);
        Assert.Single(ctx.Bus.Published.OfType<StaffAlertEvent>());
    }
}

public class ScoreInspectorTests
{
    private static readonly HostBeatmap Map = new(1754777, 845480, OsrBuilder.MapMd5, "Artist", "Title", "Insane", "mapper", 0, 1, 6.2, 1900, 200);

    private static async Task<FakeContext> Start(FakeReplays? replays = null)
    {
        var ctx = new FakeContext(new ScoreInspectorOptions { DownloadBase = "https://c.example.com/" })
            .With<IHostAccounts>(new FakeAccounts(FakeAccounts.Player(5, "Some One"), FakeAccounts.Player(2, "Staff", false, false, "gmt")))
            .With<IHostBeatmaps>(new FakeBeatmaps(Map))
            .With<IHostPerformance>(new FakePerformance(321.5))
            .With<IHostReplays>(replays ?? new FakeReplays());
        await new ScoreInspectorPlugin().InitializeAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task CommandsAreStaffOnly()
    {
        var ctx = await Start();
        Assert.Equal(["admin", "gmt", "nat"], ctx.Cmds.All["recent"].Options.RequiredGroups);
        Assert.Equal(["admin", "gmt", "nat"], ctx.Cmds.All["score"].Options.RequiredGroups);
    }

    [Fact]
    public async Task RecentListsThePlaysItKept_ByNameToo()
    {
        var ctx = await Start();
        for (int i = 1; i <= 3; i++)
            await ctx.Bus.PublishAsync(new ScoreProcessedEvent(100 + i, 5, 1754777, 0, 200 + i, "S", 0.98, 1000, 1800, true, true, i == 3, DateTimeOffset.UnixEpoch));

        var reply = await ctx.Cmds.Run("!recent some_one 2");
        Assert.NotNull(reply);
        Assert.Contains("#103 Artist - Title [Insane]", reply);
        Assert.Contains("#102", reply);
        Assert.DoesNotContain("#101", reply);
        Assert.Contains("lazer", reply);

        Assert.Contains("I don't know", await ctx.Cmds.Run("!recent nobody"));
    }

    [Fact]
    public async Task ScoreReadsTheReplay()
    {
        var replays = new FakeReplays();
        replays.ByWebsiteScore[77] = new HostReplay(OsrBuilder.Build([1, 2, 3], mods: 72, player: "Some One"), "77.osr", 5, false);
        var ctx = await Start(replays);

        var reply = await ctx.Cmds.Run("!score 77");

        Assert.Contains("Some One on Artist - Title [Insane] (b/1754777, 6.20*) +HDDT (osu!)", reply);
        Assert.Contains("1873x/1900x", reply);
        Assert.Contains("321.5pp recomputed", reply);
        Assert.Contains("stable b20240123", reply);
        Assert.Contains("https://c.example.com/inspect/replays/77", reply);
        Assert.Contains("has no replay", await ctx.Cmds.Run("!score 78"));
    }

    [Fact]
    public async Task DownloadIsForStaff()
    {
        var replays = new FakeReplays();
        replays.ByWebsiteScore[77] = new HostReplay(OsrBuilder.Build([1]), "77.osr", 5, false);
        var ctx = await Start(replays);
        var route = ctx.Routes.All["GET /inspect/replays/{scoreId}"];
        PluginHttpRequest Req(long? user) => new("GET", "/inspect/replays/77", new Dictionary<string, string> { ["scoreId"] = "77" },
            new Dictionary<string, string>(), new Dictionary<string, string>(), null, user);

        Assert.Equal(403, (await route(Req(5), default)).StatusCode);
        Assert.Equal(403, (await route(Req(null), default)).StatusCode);
        var ok = await route(Req(2), default);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal("attachment; filename=\"77.osr\"", ok.Headers!["Content-Disposition"]);
    }
}

public class ReplayCheckTests
{
    [Fact]
    public async Task FindsACopiedReplay_ButNotTheSamePlayersOwnScoreTwice()
    {
        var frames = Encoding.ASCII.GetBytes("the same compressed frames");
        var replays = new FakeReplays();
        replays.ByWebsiteScore[1] = new HostReplay(OsrBuilder.Build(frames), "1.osr", 5, false);
        replays.ByWebsiteScore[2] = new HostReplay(OsrBuilder.Build(frames, player: "thief"), "2.osr", 6, false);
        replays.ByWebsiteScore[3] = new HostReplay(OsrBuilder.Build(Encoding.ASCII.GetBytes("other frames")), "3.osr", 6, false);
        var ctx = new FakeContext(new ReplayCheckOptions())
            .With<IHostReplays>(replays)
            .With<IHostAccounts>(new FakeAccounts(FakeAccounts.Player(5, "a"), FakeAccounts.Player(6, "thief")));
        var plugin = new ReplayCheckPlugin();
        await plugin.InitializeAsync(ctx);
        var check = ctx.GetHostService<IScoreCheckService>()!;

        var first = await check.CheckAsync(1, "test");
        var again = await check.CheckAsync(1, "test");
        var copy = await check.CheckAsync(2, "test");
        var other = await check.CheckAsync(3, "test");

        Assert.Equal(0, first.Alert!.Severity);
        Assert.Equal(0, again.Alert!.Severity);
        Assert.Equal(100, copy.Alert!.Severity);
        Assert.Contains("score 1 by player #5", copy.Alert.Details);
        Assert.Equal("thief", copy.Alert.Username);
        Assert.Equal(0, other.Alert!.Severity);
        Assert.Equal("score 9 has no replay", (await check.CheckAsync(9, "test")).Problem);
        await plugin.ShutdownAsync();
    }

    [Fact]
    public async Task NewPlaysAreQueued_AndOnlyLoudOnesReachStaff()
    {
        var frames = Encoding.ASCII.GetBytes("frames");
        var replays = new FakeReplays();
        replays.ByWebsiteScore[1] = new HostReplay(OsrBuilder.Build(frames), "1.osr", 5, false);
        replays.ByWebsiteScore[2] = new HostReplay(OsrBuilder.Build(frames), "2.osr", 6, false);
        var ctx = new FakeContext(new ReplayCheckOptions()).With<IHostReplays>(replays);
        var plugin = new ReplayCheckPlugin();
        await plugin.InitializeAsync(ctx);

        ScoreProcessedEvent Play(long id, long user) => new(id, user, 1, 0, 300, "S", 0.99, 1, 1, true, true, false, DateTimeOffset.UnixEpoch);
        await ctx.Bus.PublishAsync(Play(1, 5));
        await Task.Delay(300); // the first must be stored before the copy comes in
        await ctx.Bus.PublishAsync(Play(2, 6));

        var alert = await ctx.Bus.WaitFor<StaffAlertEvent>();
        Assert.NotNull(alert);
        Assert.Equal(6, alert!.Alert.UserId);
        await Task.Delay(200);
        Assert.Single(ctx.Bus.Published.OfType<StaffAlertEvent>()); // score 1 found nothing, so staff weren't told
        await plugin.ShutdownAsync();
    }
}
