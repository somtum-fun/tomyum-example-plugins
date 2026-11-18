# pp-cap

A play over its mode's pp cap goes to staff as a `StaffAlertEvent`. With `action: restrict`, the player is also restricted,
through BanchoBot's own `!restrict`, so the staff log looks the same as when staff do it. Needs to be in
`plugins.trusted` (it reads accounts and can restrict).

Left alone: failed plays, restricted players, the anti-cheat whitelist, and the exempt groups (admin, gmt, nat and dev
unless you set others). Each play is handled once. osu!(stable) plays are checked from `ScoreSubmittedEvent` (Relax
and Autopilot get their own caps), osu!lazer plays from `ScoreProcessedEvent`. `PpCapRule` holds the decision on its
own, so it can be tested without a server.
