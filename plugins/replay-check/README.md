# replay-check

Checks each new ranked play's replay in the background and tells staff when something looks wrong. It never restricts.
Needs to be in `plugins.trusted` (it reads replays).

- **Copied replay** (built in): the compressed frames are hashed. The same frames under another player's score are
  reported (suspicion 100). This catches straight copies, not edited replays.
- **Outside analyzer** (optional, `analyzerUrl`): the replay and the map are POSTed as JSON; `../../analyzer/` is one
  built on circleguard. Its suspicion (0–1) and reasons become the alert.

Only alerts at or above `alertAt` reach staff. The plugin also offers `IScoreCheckService`, so the Discord bot's
`/scan` (or any plugin) can check a score on demand and always gets the result.
