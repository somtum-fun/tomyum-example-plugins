# TomYum example plugins

Working plugins for [TomYum](https://github.com/somtum-fun/TomYum-OSS) to copy from: how to look at players' plays and
replays, cap pp, and plug in an anti-cheat analyzer such as [circleguard](https://github.com/circleguard/circleguard).
For an empty starting point, use [tomyum-plugin-template](https://github.com/somtum-fun/tomyum-plugin-template) instead.

| Plugin | What it does | What it shows |
|---|---|---|
| [`score-inspector`](plugins/score-inspector/) | `!recent <player>` lists a player's last plays; `!score <id>` reads one play from its replay (mods, hits, combo, pp worked out again, when it was played); `GET /inspect/replays/{id}` downloads the `.osr` | staff-only commands, `IPluginStateStore`, reading a replay, the beatmap and pp host services, an HTTP route that checks who is asking |
| [`pp-cap`](plugins/pp-cap/) | a play over its mode's pp cap is sent to staff, or restricted if you turn that on; whitelisted players and staff are left alone | stable and lazer score events, `StaffAlertEvent`, running BanchoBot's `!restrict`, logic kept apart from the host so it can be tested |
| [`replay-check`](plugins/replay-check/) | checks each new play's replay: copied replays (here), plus anything an outside analyzer finds; tells staff, never restricts | a background queue, calling an HTTP service, offering `IScoreCheckService` (the Discord bot's `/scan`) |
| [`analyzer/`](analyzer/) | the outside analyzer for `replay-check`: circleguard's frame time (timewarp), unstable rate and snaps, in a small Python service | the JSON contract, so you can write your own in any language |

All three plugins only use `TomYum.Plugins.Abstractions`, the plugin API.

## Install a plugin

There are two ways; both are tested against TomYum's own plugin loader.

**Loose source** (no build step): copy the folder into the server's plugins directory and restart. The server compiles
the `.cs` files itself (`plugins.allowLooseSource: true`, the default).

```
plugins/
└── pp-cap/
    ├── plugin.json
    └── PpCapPlugin.cs
```

**Compiled**: `dotnet build -c Release` (see below), then copy the `.dll` either straight into `plugins/` or into a folder
**with the same name as the DLL**. The loader skips any other folder name:

```
plugins/
└── TomYum.Examples.PpCap/
    └── TomYum.Examples.PpCap.dll
```

All three need to be **trusted**, because they read accounts and replays, and pp-cap can restrict. Trust only works
when the server has its database:

```yaml
# config/tomyum.yaml
plugins:
  trusted:
    - tomyum.example.score-inspector
    - tomyum.example.pp-cap
    - tomyum.example.replay-check
```

A plugin that isn't trusted still loads, but it logs a warning at startup and leaves out whatever it can't do.

## Settings

Each plugin reads `plugins.<its id>` from `config/tomyum.yaml`. The same keys work as environment variables, with
`__` between the parts (e.g. `TOMYUM_PLUGINS__tomyum.example.pp-cap__Caps__osu=700` in a compose `env_file`). Put
secrets such as the analyzer key in environment variables, not in a file you commit.

```yaml
plugins:
  tomyum.example.score-inspector:
    groups: [admin, gmt, nat]            # who may use !recent, !score and the download (this is the default)
    keep: 20                             # plays remembered per player
    downloadBase: https://c.example.com  # puts a download link in !score's reply

  tomyum.example.pp-cap:
    caps:                                # a mode without a cap isn't checked
      osu: 1000
      taiko: 800
      catch: 900
      mania: 900
      osu-relax: 2500
      osu-autopilot: 1500
      "10": 500                          # a custom lazer ruleset, by its id
    action: alert                        # alert (the default) or restrict
    exemptGroups: [admin, gmt, nat, dev] # the default
    notifyPlayer: "Your play is waiting for a staff review."   # optional

  tomyum.example.replay-check:
    analyzerUrl: http://replay-analyzer:8600/analyze   # leave unset for the copied-replay check only
    # analyzerKey: from TOMYUM_PLUGINS__tomyum.example.replay-check__AnalyzerKey
    alertAt: 0.8        # tell staff from this suspicion (0-1) up
    minPp: 100          # only send plays worth at least this much to the analyzer
    rulesets: [0]       # osu! only: circleguard knows only osu!
    rankedOnly: true
    concurrency: 2
    timeoutSeconds: 60
```

Staff alerts (`StaffAlertEvent`) go wherever your server shows them. With the Discord plugin, that's a channel with
moderation buttons. Without anything listening, they still show in the server log.

## The analyzer

```bash
docker build -t replay-analyzer analyzer/
docker run -d --name replay-analyzer -p 127.0.0.1:8600:8600 -e ANALYZER_KEY=change-me replay-analyzer
# check one replay from files, without the server:
python3 analyzer/analyzer.py play.osr map.osu
```

Put it on the same Docker network as the server and set `analyzerUrl` to `http://replay-analyzer:8600/analyze`. The
thresholds are environment variables (`FRAMETIME_TIMEWARP`, `UR_SUSPICIOUS`, `SNAPS_SUSPICIOUS`). The defaults are
loose on purpose, because an analyzer that flags honest players soon gets ignored. Tune them on your own players'
replays before you trust them.

One thing we measured, and the analyzer already handles: osu!(stable) records a frame about every 16 ms, but
Relax/Autopilot replays are recorded far more often (0–5 ms on every Relax play we looked at, honest ones included).
A frame-time check on Relax would flag every Relax player as timewarping, so the analyzer skips frame time and UR for
Relax/Autopilot.

Contract, if you write your own analyzer:

```
POST /analyze   Authorization: Bearer <key>   (when set)
{"score_id": 1, "user_id": 2, "beatmap_id": 3, "ruleset_id": 0, "replay": "<base64 .osr>", "beatmap": "<base64 .osu or null>"}
→ 200 {"suspicion": 0.0-1.0, "reasons": ["shown to staff, one per line"], "fields": {"UR": "84.2"}}
```

## Build and test

```bash
git clone https://github.com/somtum-fun/TomYum-OSS ../TomYum-OSS   # for TomYum.Plugins.Abstractions
dotnet build -c Release            # or: dotnet build -c Release -p:TomYumRoot=/path/to/TomYum-OSS
dotnet test -c Release
```

`build/<Name>/` compiles `plugins/<name>/*.cs` from outside the plugin folder, so no `bin/` or `obj/` ever lands next
to the loose source. The server compiles every `.cs` file in a plugin folder, generated ones included, so never run
`dotnet build` inside a plugin folder.

`tests/` drives each plugin through a small in-memory fake of the host (`Fakes.cs`), which you can reuse for your own
plugin's tests.

## Things to know before you rely on them

- **None of these replaces a staff member.** pp-cap's `restrict` restricts automatically. Only turn it on with caps you
  are sure no honest player reaches, and expect to unrestrict people sometimes.
- The copied-replay check catches straight copies (the same compressed frames), not edited replays.
- `!recent` only knows plays made after the plugin started. It keeps up to 30 days in the plugin's state store (Redis,
  when the server has it).
- If more than one plugin offers `IScoreCheckService`, the last one to load wins.

## License

The plugins and tests: MIT, see [LICENSE](LICENSE). `analyzer/` uses circleguard (AGPL-3.0) and slider (LGPLv3+), so
it is licensed under the **AGPL-3.0** ([analyzer/LICENSE](analyzer/LICENSE)). If you run a changed copy as a service,
the AGPL asks you to offer its source to the people using it. The plugins only talk to it over HTTP and
don't include any of its code. If you write your own analyzer without circleguard, you can license it however you like.
