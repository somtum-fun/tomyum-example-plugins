# score-inspector

Look at plays from chat. Needs to be in `plugins.trusted` (it reads accounts and replays).

| | |
|---|---|
| `!recent <player\|#id> [count]` | the player's last plays (up to 10), kept from `ScoreProcessedEvent` since the plugin started |
| `!score <score id>` | one play read from its replay: map, mods, hits, combo, recomputed pp, client, when it was played |
| `GET /inspect/replays/{score id}` | the `.osr`, for signed-in staff (`requireAuth` plus a group check) |

`Osr.cs` reads a replay's header without an LZMA library; the frames stay compressed. Settings: see the main README.
